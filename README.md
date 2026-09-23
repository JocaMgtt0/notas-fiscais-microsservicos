# Emissão de Notas Fiscais em Microsserviços

[![CI](https://github.com/JocaMgtt0/notas-fiscais-microsservicos/actions/workflows/ci.yml/badge.svg)](https://github.com/JocaMgtt0/notas-fiscais-microsservicos/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![Angular](https://img.shields.io/badge/Angular-22-DD0031)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169E1)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED)

Sistema de emissão de notas fiscais dividido em dois microsserviços, **Estoque** e **Faturamento**, cada um com o próprio banco PostgreSQL, e um frontend em Angular.

O problema interessante não é o CRUD: é a **impressão da nota**. Imprimir baixa o saldo dos produtos em outro serviço, em outro banco, do outro lado da rede. Não existe transação que cubra as duas pontas, então o sistema precisa lidar com serviço fora do ar, saldo disputado por duas notas ao mesmo tempo e falha no meio do caminho, sem nunca deixar dado inconsistente.

---

## Destaques

- **Saga com compensação** na impressão: se a baixa de estoque passa e a geração do PDF falha, o saldo é estornado e a nota volta para `Aberta`
- **Resiliência com Polly**: retry, timeout e circuit breaker na chamada entre serviços. Com o Estoque fora do ar, a falha vira `503` com mensagem clara, e não um travamento
- **Concorrência tratada com lock otimista**: duas notas disputando a última unidade resultam em exatamente uma baixa, provado por teste de integração com requisições paralelas reais
- **Numeração sequencial por sequence do banco**, sem duplicidade mesmo com 20 criações simultâneas
- **Isolamento real entre serviços**: nenhuma tabela compartilhada, nenhuma chave estrangeira atravessando bancos. A nota guarda um snapshot do produto e continua abrindo com o Estoque fora do ar
- **Erros como contrato**: respostas em ProblemDetails (RFC 7807) com códigos estáveis que o frontend trata um a um
- **159 testes automatizados**, incluindo integração contra PostgreSQL real via Testcontainers, rodando em CI a cada push

---

## Stack

| Camada | Tecnologia |
|---|---|
| Frontend | Angular 22, standalone components, signals, Angular Material |
| Backend | .NET 8, ASP.NET Core |
| Banco | PostgreSQL 16, uma instância por serviço |
| ORM | Entity Framework Core |
| Resiliência | Polly (retry, timeout, circuit breaker) |
| PDF | QuestPDF |
| Logs | Serilog, JSON estruturado com correlation ID entre os serviços |
| Testes | xUnit, FluentAssertions, NSubstitute, Testcontainers |
| Orquestração | Docker Compose |
| CI | GitHub Actions |

---

## Como executar

Requer apenas Docker.

```bash
docker compose up --build
```

| Serviço | URL |
|---|---|
| Frontend | http://localhost:4200 |
| API de Faturamento (Swagger) | http://localhost:5002/swagger |
| API de Estoque (Swagger) | http://localhost:5001/swagger |

As migrations são aplicadas automaticamente na subida e o banco já vem com dados de exemplo, incluindo um produto com saldo 1 e outro com saldo 0 para demonstrar os cenários de disputa e de recusa.

---

## Arquitetura

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

O serviço de Estoque é o dono exclusivo do saldo. O Faturamento nunca lê nem escreve a tabela de produtos: precisou de saldo, chama a API.

Cada serviço segue Clean Architecture em quatro projetos, com as dependências apontando para dentro:

```
Domain          entidades e regras de negócio, sem dependência externa
Application     casos de uso, interfaces de repositório, DTOs
Infrastructure  EF Core, repositórios, HttpClient, Polly, QuestPDF
Api             controllers, injeção de dependência, middleware, Swagger
```

Como cada camada é um `.csproj` separado, a regra é garantida pelo compilador: se alguém tentar usar Entity Framework dentro do domínio, o build quebra.

---

## O fluxo de impressão

```
          +-----------+
          |  Aberta   |<---------------+
          +-----+-----+                |
                | imprimir             | falha + compensação
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

O estado `EmProcessamento` é o registro de que existe uma operação distribuída em curso. Sem ele não daria para distinguir "ninguém imprimiu ainda" de "a baixa foi enviada e o desfecho é desconhecido".

| Cenário | O que acontece | Resposta |
|---|---|---|
| Saldo insuficiente | Nota volta para `Aberta` | `422 SALDO_INSUFICIENTE`, com detalhe produto a produto |
| Outra nota levou o saldo | Nota volta para `Aberta` | `409 CONFLITO_CONCORRENCIA` |
| Estoque fora do ar | Polly esgota as tentativas, o circuito abre, nota volta para `Aberta` | `503 ESTOQUE_INDISPONIVEL` |
| Baixa passou, PDF falhou | Saldo é estornado, nota volta para `Aberta` | `500 FALHA_GERACAO_PDF` |
| Até o estorno falhou | Log crítico, nota fica em `EmProcessamento` | `500 INTERVENCAO_MANUAL` |

O último caso é deliberado: nenhuma compensação é garantida, e o sistema prefere sinalizar que precisa de intervenção a fingir que está tudo certo.

---

## Demonstrações

### Falha e recuperação entre serviços

```bash
docker compose stop estoque
```

Tente imprimir uma nota. O Faturamento esgota as tentativas, abre o circuit breaker, reverte a nota para `Aberta` e devolve `503`. A tela explica o que aconteceu e nenhum saldo é alterado.

```bash
docker compose start estoque
```

Com o serviço de volta, a mesma nota imprime normalmente.

### Duas notas disputando a última unidade

```powershell
.\scripts\demo-concorrencia.ps1
```

Cria duas notas com o mesmo produto de saldo 1 e dispara as duas impressões em paralelo. Uma fecha, a outra recebe recusa explícita, e o saldo termina em zero.

---

## Decisões técnicas

### Backend: tratamento de erros

Três camadas, e **nenhum `try/catch` em controller**.

**Exceções de domínio tipadas.** Toda violação de regra lança uma exceção derivada de `ExcecaoDeDominio` ([Estoque](backend/src/Estoque/Korp.Estoque.Domain/Excecoes/ExcecaoDeDominio.cs), [Faturamento](backend/src/Faturamento/Korp.Faturamento.Domain/Excecoes/ExcecaoDeDominio.cs)), com um `Codigo` estável como `SALDO_INSUFICIENTE`. O código é o contrato com o frontend: a tela trata por ele, nunca pelo texto da mensagem.

**Manipulador global** com `IExceptionHandler` ([Estoque](backend/src/Estoque/Korp.Estoque.Api/Middlewares/ManipuladorGlobalDeExcecoes.cs), [Faturamento](backend/src/Faturamento/Korp.Faturamento.Api/Middlewares/ManipuladorGlobalDeExcecoes.cs)). Traduz cada exceção no status HTTP correto e responde sempre em ProblemDetails, com `codigo` e `traceId`. Exceção não prevista vira 500 genérico: o detalhe vai para o log, nunca para o cliente.

**Tradução da fronteira de rede** em [ClienteHttpDeEstoque.cs](backend/src/Faturamento/Korp.Faturamento.Infrastructure/Integracao/ClienteHttpDeEstoque.cs). Acima dessa classe ninguém sabe o que é um `HttpResponseMessage` ou um circuito aberto. `BrokenCircuitException`, `TimeoutRejectedException` e `HttpRequestException` viram `EstoqueIndisponivelExcecao`; o 422 vira `SaldoInsuficienteNoEstoqueExcecao`.

Essa separação entre **recusa de negócio** e **falha técnica** atravessa o sistema: é ela que decide se o Polly tenta de novo e se a saga precisa compensar ou apenas reverter.

### Backend: consultas com LINQ e EF Core

Filtro, contagem, ordenação e paginação acontecem no banco, nunca em memória ([ProdutoRepositorio.cs](backend/src/Estoque/Korp.Estoque.Infrastructure/Persistencia/ProdutoRepositorio.cs)):

```csharp
var consulta = _contexto.Produtos.AsNoTracking();

if (!string.IsNullOrWhiteSpace(busca))
    consulta = consulta.Where(p =>
        EF.Functions.ILike(p.Codigo, termo) ||
        EF.Functions.ILike(p.Descricao, termo));

var total = await consulta.CountAsync(ct);

var itens = await consulta
    .OrderBy(p => p.Codigo)
    .Skip((pagina - 1) * tamanho)
    .Take(tamanho)
    .ToListAsync(ct);
```

`EF.Functions.ILike` vira o `ILIKE` nativo do PostgreSQL, sem `ToLower()` dos dois lados impedindo o uso de índice. `AsNoTracking()` porque listagem é leitura pura. E `Where(p => ids.Contains(p.Id))` vira `WHERE id = ANY(...)`: uma nota de 20 itens é uma ida ao banco, não vinte.

Antes de tocar no banco, a baixa consolida as quantidades por produto:

```csharp
// Se a mesma nota citar o mesmo produto em duas linhas, o que importa e a
// soma: sem isso, duas baixas parciais passariam pela validacao separadamente
// e estourariam o estoque juntas.
var quantidadesPorProduto = dto.Itens
    .GroupBy(i => i.ProdutoId)
    .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantidade));
```

### Backend: concorrência

O `Produto` tem uma coluna `versao` marcada como token de concorrência no EF Core. A baixa roda em transação, revalida o saldo e, se outra transação alterou a mesma linha, a `DbUpdateConcurrencyException` dispara um retry curto. Esgotadas as tentativas, a resposta é `409`. O saldo nunca fica negativo e nunca perde unidade ([UnidadeDeTrabalho.cs](backend/src/Estoque/Korp.Estoque.Infrastructure/Persistencia/UnidadeDeTrabalho.cs)).

### Frontend: RxJS

**`debounceTime` + `distinctUntilChanged` + `switchMap`** na busca de produtos e no autocomplete da nota. O `switchMap` cancela a requisição anterior: se o usuário digita "tec" e depois "tecl", a resposta de "tec" poderia chegar por último e sobrescrever o resultado certo. É por isso que é `switchMap` e não `mergeMap`.

**`catchError`** no [interceptor global](frontend/src/app/nucleo/interceptors.ts), que normaliza qualquer falha em um `ErroApi`. Também dentro do `switchMap` do autocomplete, onde a posição importa: ali a falha não encerra o fluxo externo, e a busca volta a funcionar quando o serviço volta.

**`forkJoin`** para carregar nota e catálogo em paralelo, com `catchError` no ramo do catálogo. Sem isso, a nota não abriria com o Estoque fora do ar, jogando fora o snapshot que o backend guarda justamente para esse caso.

**`finalize`** nos indicadores de carga, para o botão não ficar preso em "Imprimindo..." depois de uma falha. **`takeUntil`** com `Subject` de destruição em toda inscrição.

### Frontend: ciclo de vida dos componentes

- **`ngOnInit`** faz a carga inicial. Não no construtor, porque ali o `id` que vem da rota ainda não foi preenchido
- **`ngOnDestroy`** encerra as inscrições. Sem isso, uma resposta atrasada tentaria escrever em um componente que já não existe
- **`ngOnChanges`** em [itens-nota-tabela.ts](frontend/src/app/funcionalidades/notas/itens-nota-tabela.ts) recalcula totais e colunas quando os itens mudam, em vez de refazer a conta no template a cada ciclo de detecção de mudanças

A interface usa **Angular Material**: tabela com paginação, autocomplete, dialog, snackbar e indicadores de progresso.

---

## Testes

```bash
cd backend && dotnet test     # integração sobe PostgreSQL via Testcontainers, requer Docker
cd frontend && npm test
```

| Suíte | Testes | O que cobre |
|---|---|---|
| Estoque, domínio | 21 | Saldo nunca negativo, quantidade válida, baixa e estorno |
| Estoque, aplicação | 25 | Consolidação de produto repetido, unicidade de código, paginação |
| Estoque, integração | 13 | HTTP e PostgreSQL reais: CRUD, atomicidade da baixa, concorrência |
| Faturamento, domínio | 21 | Máquina de estados da nota e regras de edição |
| Faturamento, aplicação | 46 | Saga de impressão, validação de saldo, tradução de erro do cliente HTTP |
| Faturamento, integração | 13 | Numeração sob concorrência, fluxo completo, PDF, compensação |
| Frontend | 20 | Interceptors, `ngOnChanges` do componente de itens, shell |

Os que provam o que asserção de código não alcança:

- **`Duas_notas_disputando_a_ultima_unidade_apenas_uma_vence`**: duas requisições paralelas contra saldo 1. Uma passa, a outra é recusada, o saldo termina em zero
- **`Criacoes_simultaneas_nunca_repetem_numeracao`**: 20 notas em paralelo, todas com número único. A sequence resolve o que `MAX(numero) + 1` não resolveria
- **`Quando_ate_o_estorno_falha_a_nota_fica_em_processamento`**: o pior cenário da saga, sinalizado em vez de escondido
- **`Mesmo_produto_repetido_na_nota_tem_as_quantidades_somadas`**: duas linhas de 3 contra saldo 5. Individualmente cabem, somadas não
- **`AdicionarItem_considera_a_quantidade_ja_presente_na_nota`**: incluir 3 quando já há 4 exige saldo 7, não 3

O CI roda backend e frontend em máquina limpa a cada push, com warnings tratados como erro e uma trava que falha o build se nenhum teste for descoberto.

---

## Escopo e próximos passos

Ficou de fora por decisão, não por esquecimento:

- **Mensageria.** RabbitMQ com padrão Outbox tornaria a impressão assíncrona e mais robusta, ao custo de bem mais infraestrutura. REST com Polly e compensação resolve o problema com uma fração da complexidade
- **Idempotência.** O próximo passo natural: um header `Idempotency-Key` com tabela de chaves processadas, para que um retry do cliente nunca imprima a mesma nota duas vezes
- **API Gateway e autenticação.** O Angular fala com os dois serviços diretamente
- **Armazenamento do PDF.** Nota fechada é imutável, então o documento é gerado sob demanda e sai idêntico toda vez

---

## Documentação

- [ESPECIFICACAO.md](ESPECIFICACAO.md): requisitos, regras de negócio, contratos das APIs e modelo de dados, escritos antes da implementação
- [DESENVOLVIMENTO_COM_IA.md](DESENVOLVIMENTO_COM_IA.md): como a IA foi usada no desenvolvimento e os defeitos que a verificação revelou

---

Desenvolvido por **Joaquim Menegotto Vieira**.
