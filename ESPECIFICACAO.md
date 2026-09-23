# Especificação Técnica: Sistema de Emissão de Notas Fiscais

Documento de requisitos fechado antes da implementação. Onde a implementação divergiu do plano, o texto foi atualizado para refletir o que foi construído.

| Item | Decisão |
|---|---|
| Frontend | Angular 22, standalone components, signals, Angular Material |
| Backend | .NET 8, ASP.NET Core |
| Arquitetura de sistema | 2 microsserviços: Estoque e Faturamento |
| Arquitetura interna | Clean Architecture, 4 projetos por serviço |
| Banco | PostgreSQL, um por serviço, sem tabela compartilhada |
| Comunicação | REST síncrono com Polly (retry, timeout, circuit breaker) |
| Impressão | PDF real gerado no backend com QuestPDF |
| Concorrência | Lock otimista com token de versão |
| Testes | Unitários no domínio + integração no fluxo de impressão |
| Orquestração | Docker Compose |

---

## 1. Arquitetura de sistema

```
                    +---------------------------+
                    |   Angular (porta 4200)    |
                    +-------------+-------------+
                                  |
                +-----------------+-----------------+
                |                                   |
      +---------v----------+            +-----------v---------+
      | Faturamento :5002  |  REST +    |   Estoque :5001     |
      | notas fiscais      |  Polly     |   produtos e saldo  |
      +---------+----------+ ---------> +-----------+---------+
                |                                   |
      +---------v----------+            +-----------v---------+
      | faturamento_db     |            |   estoque_db        |
      +--------------------+            +---------------------+
```

Regra de ouro: **Estoque é o único dono do saldo**. Faturamento nunca lê nem escreve a tabela de produtos. Precisou de saldo, chama a API.

### Estrutura de projetos (repetida em cada serviço)

```
Korp.Estoque.Domain          entidades, value objects, exceções de domínio, sem dependência externa
Korp.Estoque.Application     casos de uso, interfaces de repositório, DTOs, validações
Korp.Estoque.Infrastructure  EF Core, repositórios, HttpClient, Polly, QuestPDF
Korp.Estoque.Api             controllers, DI, middleware de exceção, Swagger
```

Dependências apontam sempre para dentro: Api -> Infrastructure -> Application -> Domain.

---

## 2. Requisitos funcionais

### RF01 - Cadastro de produtos

Campos: código, descrição, saldo.

Critérios de aceite:
- Criar produto com código único, descrição não vazia e saldo inteiro maior ou igual a zero
- Listar produtos com busca por código ou descrição
- Editar descrição e saldo
- Código é imutável após a criação
- Excluir produto apenas se ele nunca foi usado em nota fiscal
- Tentativa de criar código duplicado retorna erro claro na tela

### RF02 - Cadastro de notas fiscais

Campos: numeração sequencial, status, itens com produto e quantidade.

Critérios de aceite:
- Nota nasce com status Aberta e número sequencial gerado automaticamente
- Numeração é global, começa em 1, sem buracos em uso normal
- Adicionar múltiplos produtos com quantidade inteira maior que zero
- Adicionar produto já presente na nota soma a quantidade na linha existente
- Editar e excluir permitido apenas em nota Aberta
- Remover item de nota Aberta
- Nota exibe código e descrição do produto sem depender do serviço de Estoque estar no ar

### RF03 - Impressão de nota fiscal

Critérios de aceite:
- Botão de impressão visível na tela de detalhe da nota
- Ao clicar, botão desabilita e exibe indicador de processamento
- Só é permitido imprimir nota com status Aberta
- Nota precisa ter ao menos um item
- Saldo de cada produto é reduzido pela quantidade da nota
- Ao concluir, status muda para Fechada e o PDF abre em nova aba
- Nota Fechada é imutável e o botão de impressão fica desabilitado
- Falha em qualquer etapa devolve a nota para Aberta e mostra mensagem específica

---

## 3. Regras de negócio

| ID | Regra |
|---|---|
| RN01 | Código de produto é único no sistema e imutável após a criação |
| RN02 | Saldo de produto nunca fica negativo |
| RN03 | Quantidade de item de nota é inteiro maior que zero |
| RN04 | Numeração de nota é sequencial, global e gerada por sequence do banco |
| RN05 | Baixa de estoque ocorre apenas na impressão, nunca na criação da nota. Não há reserva |
| RN06 | Nota Fechada é imutável: itens, status e numeração |
| RN07 | Somente nota Aberta pode ser editada, excluída ou impressa |
| RN08 | Nota sem itens não pode ser impressa |
| RN09 | Produto já referenciado por qualquer nota não pode ser excluído |
| RN10 | Baixa de estoque é atômica: ou todos os itens baixam, ou nenhum |
| RN11 | Item de nota guarda snapshot de código e descrição do produto no momento da inclusão |
| RN12 | Saldo é validado na inclusão do item (feedback rápido) e revalidado na impressão (fonte da verdade) |

RN05 e RN12 juntas são o que cria o cenário de concorrência: entre incluir o item e imprimir, o saldo pode ter mudado.

---

## 4. Máquina de estados da nota

```
              criar
                |
                v
          +-----------+
          |  Aberta   |<---------------+
          +-----+-----+                |
                |                      | falha (negócio ou técnica)
                | imprimir             | + compensação
                v                      |
       +------------------+            |
       | EmProcessamento  |------------+
       +--------+---------+
                | baixa confirmada + PDF gerado
                v
          +-----------+
          |  Fechada  |  (terminal, imutável)
          +-----------+
```

O status `EmProcessamento` não é um requisito de negócio, mas é o que torna a recuperação de falha possível. Ele é o registro de que existe uma operação distribuída em andamento.

---

## 5. Fluxo de impressão (o coração do sistema)

```
Angular            Faturamento                      Estoque
   |                    |                              |
   |-- POST imprimir -->|                              |
   |                    |-- valida status = Aberta     |
   |                    |-- valida tem itens           |
   |                    |-- status = EmProcessamento   |
   |                    |   (commit)                   |
   |                    |                              |
   |                    |-- POST /produtos/baixa ------>|
   |                    |   (Polly: 3 retries,          |-- transação
   |                    |    timeout 5s,                |-- lock otimista
   |                    |    circuit breaker)           |-- valida saldo
   |                    |<---------- 200 OK ------------|-- decrementa
   |                    |                              |
   |                    |-- gera PDF (QuestPDF)        |
   |                    |-- status = Fechada           |
   |<-- 200 + urlPdf ---|                              |
```

### Tratamento de cada falha

| Cenário | Resposta do Estoque | Ação do Faturamento | HTTP para o Angular |
|---|---|---|---|
| Saldo insuficiente | 422 com lista de produtos faltantes | Volta nota para Aberta | 422 `SALDO_INSUFICIENTE` com detalhe por produto |
| Conflito de concorrência após retries | 409 | Volta nota para Aberta | 409 `CONFLITO_CONCORRENCIA` |
| Estoque fora do ar (timeout, connection refused, 5xx) | nenhuma | Polly esgota retries, circuit breaker abre, volta nota para Aberta | 503 `ESTOQUE_INDISPONIVEL` |
| Circuit breaker já aberto | nenhuma | Falha imediata sem chamar | 503 `ESTOQUE_INDISPONIVEL` |
| Baixa deu certo mas PDF falhou | 200 | Chama `POST /produtos/estorno`, volta nota para Aberta | 500 `FALHA_GERACAO_PDF` |
| Estorno também falhou | erro | Log crítico, nota permanece EmProcessamento | 500 `INTERVENCAO_MANUAL` |

Esse último caso é deliberado: nenhuma compensação é 100% garantida, e o sistema sinaliza a necessidade de intervenção em vez de esconder o problema.

### Como demonstrar

`docker compose stop estoque`, clicar em imprimir, ver a mensagem de indisponibilidade e a nota continuando Aberta. Subir o serviço de volta e imprimir com sucesso.

---

## 6. Tratamento de concorrência

Cenário: produto com saldo 1 disputado por duas notas ao mesmo tempo.

Implementação no serviço de Estoque:
- Coluna `Versao` na entidade Produto, configurada como `IsConcurrencyToken()` no EF Core
- A baixa roda dentro de uma transação e revalida o saldo antes de decrementar
- `DbUpdateConcurrencyException` dispara retry curto (3 tentativas com backoff de 50ms)
- Esgotadas as tentativas, retorna 409

Resultado esperado: das duas notas, uma fecha e a outra recebe `SALDO_INSUFICIENTE` ou `CONFLITO_CONCORRENCIA`. O saldo nunca fica negativo e nunca perde unidade.

Demonstração: `scripts/demo-concorrencia.ps1` dispara duas impressões em paralelo. Duas abas de navegador não serviriam, porque a janela de disputa é de milissegundos.

---

## 7. Contratos de API

### Estoque (porta 5001)

```
GET    /api/produtos?busca=&pagina=&tamanho=
GET    /api/produtos/{id}
POST   /api/produtos
PUT    /api/produtos/{id}
DELETE /api/produtos/{id}
POST   /api/produtos/consultar-saldo    { produtoIds: [] }
POST   /api/produtos/baixa              { notaId, itens: [{ produtoId, quantidade }] }
POST   /api/produtos/estorno            { notaId, itens: [{ produtoId, quantidade }] }
GET    /health
```

### Faturamento (porta 5002)

```
GET    /api/notas?status=&pagina=&tamanho=
GET    /api/notas/{id}
POST   /api/notas
DELETE /api/notas/{id}
POST   /api/notas/{id}/itens            { produtoId, quantidade }
PUT    /api/notas/{id}/itens/{itemId}   { quantidade }
DELETE /api/notas/{id}/itens/{itemId}
POST   /api/notas/{id}/imprimir
GET    /api/notas/{id}/pdf
GET    /health
```

O Angular consome os dois serviços diretamente. Sem API Gateway, decisão consciente para não inflar o escopo.

### Padrão de erro (ProblemDetails, RFC 7807)

```json
{
  "title": "Saldo insuficiente",
  "status": 422,
  "detail": "O produto PRD-001 possui saldo 3 e a nota requer 5 unidades.",
  "codigo": "SALDO_INSUFICIENTE",
  "traceId": "00-4bf92f-01",
  "erros": [
    { "produtoCodigo": "PRD-001", "saldoDisponivel": 3, "quantidadeSolicitada": 5 }
  ]
}
```

Códigos de domínio: `PRODUTO_CODIGO_DUPLICADO`, `PRODUTO_NAO_ENCONTRADO`, `PRODUTO_EM_USO`, `NOTA_NAO_ENCONTRADA`, `NOTA_STATUS_INVALIDO`, `NOTA_SEM_ITENS`, `QUANTIDADE_INVALIDA`, `SALDO_INSUFICIENTE`, `CONFLITO_CONCORRENCIA`, `ESTOQUE_INDISPONIVEL`, `FALHA_GERACAO_PDF`.

---

## 8. Modelo de dados

### estoque_db

**produtos**

| Coluna | Tipo | Observação |
|---|---|---|
| id | uuid | PK |
| codigo | varchar(50) | unique, imutável |
| descricao | varchar(200) | not null |
| saldo | int | check >= 0 |
| versao | int | concurrency token |
| criado_em | timestamptz | |
| atualizado_em | timestamptz | |

**movimentacoes_estoque** (trilha de auditoria, também sustenta a checagem de RN09)

| Coluna | Tipo | Observação |
|---|---|---|
| id | uuid | PK |
| produto_id | uuid | FK |
| nota_id | uuid | referência lógica, sem FK entre bancos |
| tipo | varchar(10) | BAIXA ou ESTORNO |
| quantidade | int | |
| saldo_anterior | int | |
| saldo_posterior | int | |
| ocorrido_em | timestamptz | |

### faturamento_db

**notas_fiscais**

| Coluna | Tipo | Observação |
|---|---|---|
| id | uuid | PK |
| numero | bigint | unique, de sequence |
| status | varchar(20) | Aberta, EmProcessamento, Fechada |
| criada_em | timestamptz | |
| fechada_em | timestamptz | nullable |

**itens_nota_fiscal**

| Coluna | Tipo | Observação |
|---|---|---|
| id | uuid | PK |
| nota_fiscal_id | uuid | FK, cascade |
| produto_id | uuid | referência lógica |
| produto_codigo | varchar(50) | snapshot |
| produto_descricao | varchar(200) | snapshot |
| quantidade | int | check > 0 |

Nenhuma FK atravessa os bancos. Isso é intencional e é o ponto que prova que são microsserviços de verdade.

---

## 9. Frontend Angular

### Telas

1. **Produtos**: tabela com busca, paginação, botões de novo, editar e excluir. Formulário em dialog do Material
2. **Notas fiscais**: tabela com filtro por status, coluna de número, status e total de itens
3. **Detalhe da nota**: cabeçalho com número e status, tabela de itens, adicionar item por autocomplete de produto, botão Imprimir com spinner

### Uso deliberado de recursos do Angular

**Ciclos de vida**
- `ngOnInit`: carga inicial das listas
- `ngOnDestroy` com `takeUntilDestroyed` ou `Subject` de destruição: cancelar subscriptions
- `ngOnChanges`: componente de item de nota reagindo a mudança do `@Input`

**RxJS**
- `debounceTime` + `distinctUntilChanged` + `switchMap` no autocomplete de produtos
- `catchError` no `HttpInterceptor` global, traduzindo ProblemDetails em toast
- `finalize` para desligar o spinner do botão de impressão em qualquer desfecho
- `forkJoin` para carregar nota e catálogo de produtos em paralelo

**Outros**
- Reactive Forms com validadores customizados
- `HttpInterceptor` de correlation ID e de erro
- Signals para estado local dos componentes

---

## 10. Requisitos não funcionais

- `docker compose up` sobe tudo: dois Postgres, dois serviços, front
- Migrations aplicadas automaticamente no start
- Seed com 10 produtos e 2 notas, para a demo não começar vazia
- Swagger em ambos os serviços
- Serilog com log estruturado em JSON
- Correlation ID propagado do Angular ao Faturamento e ao Estoque, presente em todos os logs
- Health checks em `/health`
- Middleware global de exceção, nenhuma stack trace vazando para o cliente
- Validação de entrada nas entidades de domínio, sem duplicar a regra na borda da API

---

## 11. Estratégia de testes

**Unitários (xUnit + FluentAssertions + NSubstitute)**

Domínio:
- Nota nasce Aberta
- Nota Fechada rejeita alteração de item
- Nota sem item rejeita impressão
- Transição de status inválida lança exceção de domínio
- Item com quantidade zero ou negativa é rejeitado
- Produto repetido soma quantidade
- Produto rejeita saldo negativo

Aplicação:
- Caso de uso de impressão com Estoque respondendo erro devolve nota para Aberta
- Caso de uso de impressão com falha de PDF dispara estorno

**Integração (WebApplicationFactory + Testcontainers)**
- Fluxo completo de impressão com sucesso, verificando saldo no banco de Estoque
- Impressão com saldo insuficiente, verificando que nada mudou nos dois bancos
- Duas baixas concorrentes no mesmo produto com saldo 1: exatamente uma vence

---

## 12. Fora de escopo

Autenticação, autorização, multiusuário, API Gateway, cancelamento ou estorno de nota fechada, relatórios, mensageria, idempotência por header, funcionalidade de IA. Documentado por decisão, não por esquecimento.
