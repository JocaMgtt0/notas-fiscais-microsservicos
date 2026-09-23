# Desenvolvimento assistido por IA

Este documento registra, de forma aberta, como a IA foi usada na construção deste projeto. Ele existe porque acho que a pergunta "você usou IA?" merece resposta clara e verificável, e não uma omissão.

**Resumo:** usei Claude Code como par de programação ao longo de todo o desenvolvimento. As decisões de arquitetura, de escopo e de prioridade foram minhas. A verificação foi feita executando o sistema, não confiando no que foi gerado.

---

## Ferramenta e formato de trabalho

Claude Code em sessão interativa, com acesso ao terminal, ao Docker e ao navegador. Isso importa: não foi um assistente sugerindo trechos de código em uma janela separada. Ele executava os comandos, subia os containers, chamava as APIs e abria as telas, e o resultado real de cada execução alimentava a etapa seguinte.

O trabalho foi organizado em sete etapas, e cada uma virou um commit. O histórico do repositório é o registro fiel dessa sequência.

---

## O que foi decidido por mim

Antes de escrever a primeira linha de código, fechamos uma especificação completa: requisitos funcionais com critérios de aceite, 12 regras de negócio numeradas, máquina de estados da nota, contratos das duas APIs, modelo de dados e estratégia de testes. Está em [ESPECIFICACAO.md](ESPECIFICACAO.md), e foi escrita antes da implementação, não depois.

As escolhas que definiram o projeto foram minhas:

| Decisão | Alternativa descartada | Por quê |
|---|---|---|
| C# com .NET 8 | Go | Venho de Java, e a proximidade reduz o risco de erro conceitual em um prazo curto de sete dias |
| REST síncrono com Polly | RabbitMQ com padrão Outbox | Mais robusto, mas somaria dois dias e o prazo era de sete |
| PDF real com QuestPDF | Apenas simular o processamento | Imprimir é o núcleo do fluxo, e um documento de verdade torna a demonstração honesta |
| Priorizar concorrência | Idempotência | O cenário de disputa pelo saldo é o risco mais real do fluxo de impressão |
| Sem cliente e sem valores na nota | Inventar campos para o PDF ficar cheio | O domínio foi definido como numeração, status e produtos. Escopo extra sem demanda é risco |

Também foi minha a decisão de escrever os testes junto com cada camada, e não deixá-los para o fim. O argumento que me convenceu: teste no fim é o primeiro item a ser cortado quando o prazo aperta.

---

## O que a IA fez

Escreveu a maior parte do código, seguindo a especificação acordada. Escreveu também os testes, a documentação e os arquivos de infraestrutura.

Mais útil do que isso: **executou e verificou**. Subiu os containers, aplicou as migrations, chamou os endpoints, abriu as telas no navegador, derrubou o serviço de Estoque para ver o que acontecia. É dessa execução que veio o valor real, porque foi ela que revelou os defeitos abaixo.

---

## Os defeitos que a execução revelou

Esta é a parte que considero mais relevante do processo. Todo o código compilava e passava nos testes de unidade antes de cada um destes bugs aparecer. Nenhum deles seria encontrado por revisão de leitura.

### 1. O Entity Framework gerava `UPDATE` onde deveria ser `INSERT`

Apareceu na primeira vez que o sistema rodou em container, ao adicionar um item a uma nota.

As entidades geram o próprio `Guid` no construtor. Para chave `Guid`, o EF Core assume por convenção `ValueGeneratedOnAdd` e aplica a regra "chave preenchida significa registro que já existe". A nota funcionava porque entrava por `Add()` explícito, o que força o estado `Added`. O item entrava pela coleção de navegação, e aí a heurística decidia errado: emitia `UPDATE`, que afetava zero linhas.

O mesmo defeito existia em `MovimentacaoEstoque` e ainda não tinha aparecido, porque nenhuma baixa havia passado pela API. Um bug, dois serviços.

Corrigido com `ValueGeneratedNever()` nas quatro entidades. Commit `b421e01`.

### 2. O circuit breaker nunca abria

Descoberto ao testar o cenário de falha: derrubei o serviço de Estoque e a segunda tentativa de impressão levou os mesmos 17 segundos da primeira, quando deveria falhar instantaneamente.

Causa: a sobrecarga de `AddPolicyHandler` que recebe uma fábrica executa essa fábrica **a cada requisição**. Um circuit breaker novo, zerado, era criado toda vez. Circuit breaker é stateful e precisa ser a mesma instância para acumular falhas. O retry não tem esse problema porque é stateless.

Corrigido registrando o breaker como singleton. Depois disso, o comportamento observado:

```
tentativa 1: 503 em 17.7s   1 chamada + 3 retries, cada uma no timeout de 5s
tentativa 2: 503 em  4.2s   circuito abre no meio
tentativa 3: 503 em  0s     falha instantânea
```

### 3. A tela da nota não abria com o Estoque fora do ar

Encontrado testando o frontend no navegador contra os serviços reais.

`forkJoin` falha inteiro se qualquer fonte falhar. A tela de detalhe carregava nota e catálogo em paralelo, e a falha do catálogo derrubava tudo, redirecionando para a lista.

Isso anulava na prática uma decisão central da arquitetura: cada item da nota guarda cópia do código e da descrição do produto justamente para não depender do outro serviço. O backend fazia a parte dele, e o frontend jogava fora.

Corrigido com `catchError` no ramo do catálogo. Commit `27b7857`.

### 4. A mensagem útil era sobrescrita por uma genérica

Ainda no teste do cenário de falha. Ao falhar a impressão, a tela recarregava tudo; a chamada ao catálogo falhava em seguida, e o aviso "não foi possível falar com o servidor" cobria o "o serviço de Estoque está indisponível, a nota permanece aberta e nenhum saldo foi alterado".

Só apareceu porque instrumentei a página para capturar o texto do aviso no momento em que ele surgia, em vez de tentar fotografar a tela no instante certo.

### 5. Uma dependência declarada e nunca usada

Ao escrever o detalhamento técnico, que exige justificar cada biblioteca, descobri que `FluentValidation` estava declarado nos dois serviços e não era usado em lugar nenhum. Sobra do scaffolding inicial.

Removido. A resposta honesta ficou melhor do que a original: a validação vive nas entidades de domínio, e uma biblioteca de validação na borda da API duplicaria a regra em dois lugares.

### 6. Um teste quebrado no repositório

O `app.spec.ts` gerado pelo `ng new` afirmava existir um `<h1>` com o texto `'Hello, frontend'`. Reescrevi o shell da aplicação e não ajustei o teste. Os dois casos falhavam, e o segundo por motivo pior: sem `provideRouter`, o `routerLink` derrubava o componente.

Passou despercebido porque o pipeline só rodava `dotnet test`. Corrigido, e o pipeline ganhou um job de frontend com build e testes, para que o problema não se repita.

---

## Verificação

O que sustenta a confiança neste código não é a origem dele, é o que foi feito para conferi-lo.

**159 testes automatizados**, 139 no backend e 20 no frontend. Os de integração sobem um PostgreSQL real em container via Testcontainers e exercitam concorrência de verdade, com requisições paralelas.

**Integração contínua** rodando a cada push, em máquina limpa, com dois jobs. Warnings tratados como erro no backend, e uma trava que faz o build falhar se nenhum teste for descoberto, para evitar o selo verde que não significa nada.

**Verificação manual do sistema rodando**, incluindo o ciclo completo de falha e recuperação: derrubar o serviço de Estoque, confirmar que a nota permanece Aberta e que nenhum saldo foi alterado, religar o serviço e imprimir a mesma nota com sucesso.

---

## Limites, ditos com clareza

A IA errou nos seis casos acima, e provavelmente em outros que ainda não apareceram. Ela acertou a estrutura e escreveu muito código correto, mas quem decidiu o escopo, quem cobrou a verificação e quem sabe explicar cada decisão fui eu.

O histórico de commits mostra a evolução real do trabalho, incluindo os commits de correção. Não houve reescrita de história para esconder os erros, porque os erros e as correções são parte do que aconteceu.

Joaquim Menegotto Vieira
