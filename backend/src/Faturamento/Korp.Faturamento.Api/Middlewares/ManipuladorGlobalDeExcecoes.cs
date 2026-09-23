using Korp.Faturamento.Application.Excecoes;
using Korp.Faturamento.Domain.Excecoes;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Korp.Faturamento.Api.Middlewares;

/// <summary>
/// Ponto unico de traducao de excecao em resposta HTTP.
///
/// Nenhum controller tem try/catch. O caso de uso lanca excecao tipada e este
/// manipulador escolhe o status e o corpo, sempre em ProblemDetails (RFC 7807).
///
/// A escolha de status importa para o frontend saber o que dizer ao usuario:
///
///   422 SALDO_INSUFICIENTE     nao adianta repetir, falta estoque
///   409 CONFLITO_CONCORRENCIA  outra nota levou o saldo, vale tentar de novo
///   503 ESTOQUE_INDISPONIVEL   problema temporario, vale tentar de novo
///   500 INTERVENCAO_MANUAL     precisa de gente, nao adianta insistir
/// </summary>
public class ManipuladorGlobalDeExcecoes : IExceptionHandler
{
    private readonly ILogger<ManipuladorGlobalDeExcecoes> _logger;

    public ManipuladorGlobalDeExcecoes(ILogger<ManipuladorGlobalDeExcecoes> logger) =>
        _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext contexto, Exception excecao, CancellationToken ct)
    {
        var problema = Traduzir(excecao);

        if (problema.Status >= 500)
            _logger.LogError(excecao, "Falha em {Caminho}", contexto.Request.Path);
        else
            _logger.LogWarning("Regra violada em {Caminho}: {Mensagem}",
                contexto.Request.Path, excecao.Message);

        problema.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? contexto.TraceIdentifier;

        contexto.Response.StatusCode = problema.Status!.Value;
        await contexto.Response.WriteAsJsonAsync(problema, ct);

        return true;
    }

    private static ProblemDetails Traduzir(Exception excecao) => excecao switch
    {
        // O Estoque recusou por falta de saldo. Devolve o detalhamento produto
        // a produto para a tela poder apontar exatamente o que faltou.
        SaldoInsuficienteNoEstoqueExcecao e => Montar(
            StatusCodes.Status422UnprocessableEntity, "Saldo insuficiente", e,
            new Dictionary<string, object?>
            {
                ["faltas"] = e.Faltas.Select(f => new
                {
                    produtoCodigo = f.ProdutoCodigo,
                    saldoDisponivel = f.SaldoDisponivel,
                    quantidadeSolicitada = f.QuantidadeSolicitada
                })
            }),

        // Cenario central de falha: o servico caiu, a nota voltou para
        // Aberta e o usuario recebe uma mensagem que explica o que aconteceu.
        EstoqueIndisponivelExcecao e =>
            Montar(StatusCodes.Status503ServiceUnavailable, "Servico de Estoque indisponivel", e),

        ConflitoDeConcorrenciaExcecao e =>
            Montar(StatusCodes.Status409Conflict, "Conflito de concorrencia", e),

        FalhaGeracaoPdfExcecao e =>
            Montar(StatusCodes.Status500InternalServerError, "Falha ao gerar o PDF", e),

        IntervencaoManualNecessariaExcecao e => Montar(
            StatusCodes.Status500InternalServerError, "Intervencao manual necessaria", e,
            new Dictionary<string, object?>
            {
                ["notaId"] = e.NotaId,
                ["numero"] = e.Numero
            }),

        StatusInvalidoExcecao e => Montar(
            StatusCodes.Status409Conflict, "Status da nota nao permite a operacao", e,
            new Dictionary<string, object?> { ["statusAtual"] = e.StatusAtual }),

        NotaSemItensExcecao e =>
            Montar(StatusCodes.Status422UnprocessableEntity, "Nota sem itens", e),

        NotaNaoEncontradaExcecao e =>
            Montar(StatusCodes.Status404NotFound, "Nota nao encontrada", e),

        ItemNaoEncontradoExcecao e =>
            Montar(StatusCodes.Status404NotFound, "Item nao encontrado", e),

        QuantidadeInvalidaExcecao e =>
            Montar(StatusCodes.Status400BadRequest, "Quantidade invalida", e),

        DadosInvalidosExcecao e =>
            Montar(StatusCodes.Status400BadRequest, "Dados invalidos", e),

        ExcecaoDeDominio e =>
            Montar(StatusCodes.Status400BadRequest, "Regra de negocio violada", e),

        OperationCanceledException => new ProblemDetails
        {
            Status = 499,
            Title = "Requisicao cancelada"
        },

        _ => new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Erro interno",
            Detail = "Ocorreu uma falha inesperada ao processar a requisicao."
        }
    };

    private static ProblemDetails Montar(
        int status, string titulo, ExcecaoDeDominio excecao,
        IDictionary<string, object?>? extras = null)
    {
        var problema = new ProblemDetails
        {
            Status = status,
            Title = titulo,
            Detail = excecao.Message
        };

        problema.Extensions["codigo"] = excecao.Codigo;

        if (extras is not null)
            foreach (var (chave, valor) in extras)
                problema.Extensions[chave] = valor;

        return problema;
    }
}
