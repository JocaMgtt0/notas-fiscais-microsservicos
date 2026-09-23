using Korp.Faturamento.Application.Contratos;
using Korp.Faturamento.Application.Servicos;
using Korp.Faturamento.Infrastructure.Integracao;
using Korp.Faturamento.Infrastructure.Pdf;
using Korp.Faturamento.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Korp.Faturamento.Infrastructure;

public static class InjecaoDeDependencia
{
    public static IServiceCollection AdicionarInfraestrutura(
        this IServiceCollection servicos, IConfiguration configuracao)
    {
        var conexao = configuracao.GetConnectionString("Padrao")
            ?? throw new InvalidOperationException("Connection string 'Padrao' nao configurada.");

        servicos.AddDbContext<FaturamentoDbContext>(opcoes => opcoes.UseNpgsql(conexao));

        servicos.AddScoped<IRepositorioDeNotas, RepositorioDeNotas>();
        servicos.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();
        servicos.AddSingleton<IGeradorDePdf, GeradorDePdfQuestPdf>();

        servicos.AddScoped<ServicoDeNotas>();
        servicos.AddScoped<ServicoDeImpressao>();

        AdicionarClienteDeEstoque(servicos, configuracao);

        return servicos;
    }

    /// <summary>
    /// Registra o cliente do servico de Estoque com as tres politicas de
    /// resiliencia (retry, timeout e circuit breaker).
    ///
    /// A ordem do registro define o aninhamento, e a primeira politica
    /// adicionada e a mais externa:
    ///
    ///     retry  ->  circuit breaker  ->  timeout  ->  chamada HTTP
    ///
    /// Por que nesta ordem:
    ///
    /// O timeout fica por dentro para valer **por tentativa**. Se ficasse por
    /// fora, uma unica janela de 5 segundos teria que caber as tres tentativas,
    /// e o retry perderia sentido.
    ///
    /// O circuit breaker fica entre os dois, contando cada tentativa
    /// individual. Com isso, duas impressoes seguidas contra um Estoque fora
    /// do ar ja abrem o circuito, e a terceira falha instantaneamente em vez
    /// de gastar mais quinze segundos esperando. E exatamente o comportamento
    /// que se quer demonstrar: o sistema para de bater em porta fechada.
    /// </summary>
    private static void AdicionarClienteDeEstoque(
        IServiceCollection servicos, IConfiguration configuracao)
    {
        var secao = configuracao.GetSection("ServicoEstoque");

        var baseUrl = secao["BaseUrl"] ?? "http://localhost:5001";
        var timeout = int.TryParse(secao["TimeoutSegundos"], out var t) ? t : 5;
        var tentativas = int.TryParse(secao["TentativasRetry"], out var r) ? r : 3;
        var falhasParaAbrir = int.TryParse(secao["FalhasParaAbrirCircuito"], out var f) ? f : 5;
        var segundosAberto = int.TryParse(secao["SegundosCircuitoAberto"], out var s) ? s : 15;

        // O circuit breaker precisa ser UMA instancia compartilhada por toda a
        // aplicacao. Ele e stateful: conta falhas ao longo do tempo para
        // decidir quando abrir. A sobrecarga de AddPolicyHandler que recebe
        // uma fabrica executa essa fabrica A CADA REQUISICAO, o que criaria um
        // breaker zerado toda vez e o circuito nunca abriria.
        //
        // O retry nao tem esse problema porque e stateless: cada requisicao
        // conta as proprias tentativas, entao pode ser criado por chamada.
        servicos.AddSingleton(provedor => new CircuitoDoEstoque(
            PoliticaDeCircuitBreaker(provedor, falhasParaAbrir, segundosAberto)));

        servicos.AddHttpClient<IServicoDeEstoque, ClienteHttpDeEstoque>(cliente =>
            {
                cliente.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

                // Margem sobre o timeout do Polly. Quem deve interromper a
                // chamada e a politica, nao o HttpClient, para que a excecao
                // chegue como TimeoutRejectedException e nao como um
                // TaskCanceledException generico.
                cliente.Timeout = TimeSpan.FromSeconds(timeout * tentativas + 10);
            })
            .AddPolicyHandler((provedor, _) => PoliticaDeRetry(provedor, tentativas))
            .AddPolicyHandler((provedor, _) =>
                provedor.GetRequiredService<CircuitoDoEstoque>().Politica)
            .AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(
                TimeSpan.FromSeconds(timeout)));
    }

    /// <summary>
    /// Invólucro que existe apenas para dar um tipo proprio ao circuit breaker
    /// e assim registra-lo como singleton sem colidir com outras politicas.
    /// </summary>
    private sealed class CircuitoDoEstoque
    {
        public CircuitoDoEstoque(IAsyncPolicy<HttpResponseMessage> politica) => Politica = politica;

        public IAsyncPolicy<HttpResponseMessage> Politica { get; }
    }

    private static IAsyncPolicy<HttpResponseMessage> PoliticaDeRetry(
        IServiceProvider provedor, int tentativas)
    {
        var logger = provedor.GetRequiredService<ILoggerFactory>().CreateLogger("Resiliencia");

        return HttpPolicyExtensions
            // HandleTransientHttpError cobre 5xx e falha de rede. O 408 entra
            // separado. Note que 422 e 409 NAO estao aqui de proposito: sao
            // recusas de negocio, e repetir "sem saldo" tres vezes so gastaria
            // tempo para receber a mesma resposta.
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount: tentativas,
                sleepDurationProvider: tentativa =>
                    TimeSpan.FromMilliseconds(200 * Math.Pow(2, tentativa - 1)),
                onRetry: (resultado, espera, tentativa, _) =>
                    logger.LogWarning(
                        "Tentativa {Tentativa} para o servico de Estoque falhou ({Motivo}). " +
                        "Nova tentativa em {Espera}ms.",
                        tentativa,
                        resultado.Exception?.GetType().Name
                            ?? resultado.Result?.StatusCode.ToString() ?? "desconhecido",
                        espera.TotalMilliseconds));
    }

    private static IAsyncPolicy<HttpResponseMessage> PoliticaDeCircuitBreaker(
        IServiceProvider provedor, int falhasParaAbrir, int segundosAberto)
    {
        var logger = provedor.GetRequiredService<ILoggerFactory>().CreateLogger("Resiliencia");

        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: falhasParaAbrir,
                durationOfBreak: TimeSpan.FromSeconds(segundosAberto),
                onBreak: (_, duracao) => logger.LogError(
                    "Circuito ABERTO para o servico de Estoque por {Segundos}s. " +
                    "Novas chamadas falharao imediatamente.", duracao.TotalSeconds),
                onReset: () => logger.LogInformation(
                    "Circuito FECHADO para o servico de Estoque. Chamadas normalizadas."),
                onHalfOpen: () => logger.LogInformation(
                    "Circuito MEIO ABERTO. Testando o servico de Estoque com uma chamada."));
    }

    public static async Task PrepararBancoAsync(
        this IServiceProvider provedor, CancellationToken ct = default)
    {
        using var escopo = provedor.CreateScope();

        var contexto = escopo.ServiceProvider.GetRequiredService<FaturamentoDbContext>();
        var logger = escopo.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(PrepararBancoAsync));

        logger.LogInformation("Aplicando migrations do banco de Faturamento.");
        await contexto.Database.MigrateAsync(ct);
    }
}
