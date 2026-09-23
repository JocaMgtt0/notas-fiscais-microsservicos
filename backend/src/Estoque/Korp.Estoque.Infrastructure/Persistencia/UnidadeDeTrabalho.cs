using Korp.Estoque.Application.Contratos;
using Korp.Estoque.Domain.Excecoes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Korp.Estoque.Infrastructure.Persistencia;

/// <summary>
/// Excecao lancada quando a concorrencia otimista nao se resolve nem apos
/// as novas tentativas. Vira HTTP 409 na API.
/// </summary>
public sealed class ConflitoDeConcorrenciaExcecao : ExcecaoDeDominio
{
    public ConflitoDeConcorrenciaExcecao()
        : base("CONFLITO_CONCORRENCIA",
               "O produto foi alterado por outra operacao simultanea. Tente novamente.") { }
}

/// <summary>
/// Unidade de trabalho com tratamento de concorrencia otimista.
///
/// Resolve o cenario classico de disputa: produto com saldo 1 disputado
/// por duas notas ao mesmo tempo.
///
/// Como funciona:
///
/// 1. Cada Produto tem uma coluna "versao", marcada como token de concorrencia.
/// 2. Ao gravar, o EF emite algo como
///       UPDATE produtos SET saldo = 0, versao = 3 WHERE id = ... AND versao = 2
/// 3. Se outra transacao ja tinha incrementado a versao, o WHERE nao casa,
///    zero linhas sao afetadas e o EF lanca DbUpdateConcurrencyException.
/// 4. Aqui a transacao e desfeita, o change tracker e limpo e a operacao inteira
///    roda de novo, agora lendo o saldo atualizado.
/// 5. Na nova tentativa, se o saldo ja nao for suficiente, a propria entidade
///    lanca SaldoInsuficienteExcecao. E assim que "uma nota fecha e a outra
///    recebe erro" acontece, sem lock pessimista e sem saldo negativo.
///
/// Limpar o change tracker entre tentativas nao e detalhe: sem isso, a segunda
/// tentativa reencontraria as mesmas entidades em memoria, com o mesmo valor
/// velho de versao, e falharia de novo indefinidamente.
/// </summary>
public class UnidadeDeTrabalho : IUnidadeDeTrabalho
{
    private const int MaximoDeTentativas = 3;
    private static readonly TimeSpan EsperaEntreTentativas = TimeSpan.FromMilliseconds(50);

    private readonly EstoqueDbContext _contexto;
    private readonly ILogger<UnidadeDeTrabalho> _logger;

    public UnidadeDeTrabalho(EstoqueDbContext contexto, ILogger<UnidadeDeTrabalho> logger)
    {
        _contexto = contexto;
        _logger = logger;
    }

    public Task<int> SalvarAsync(CancellationToken ct = default) =>
        _contexto.SaveChangesAsync(ct);

    public async Task ExecutarEmTransacaoAsync(Func<Task> operacao, CancellationToken ct = default)
    {
        for (var tentativa = 1; tentativa <= MaximoDeTentativas; tentativa++)
        {
            await using var transacao = await _contexto.Database.BeginTransactionAsync(ct);

            try
            {
                await operacao();
                await transacao.CommitAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transacao.RollbackAsync(ct);
                _contexto.ChangeTracker.Clear();

                if (tentativa == MaximoDeTentativas)
                {
                    _logger.LogWarning(
                        "Conflito de concorrencia nao resolvido apos {Tentativas} tentativas.",
                        MaximoDeTentativas);

                    throw new ConflitoDeConcorrenciaExcecao();
                }

                _logger.LogInformation(
                    "Conflito de concorrencia na tentativa {Tentativa}. Repetindo a operacao.",
                    tentativa);

                await Task.Delay(EsperaEntreTentativas, ct);
            }
            catch
            {
                // Qualquer outra falha, incluindo violacao de regra de negocio,
                // desfaz a transacao e sobe. Repetir nao ajudaria: saldo
                // insuficiente continuara insuficiente na proxima tentativa.
                await transacao.RollbackAsync(ct);
                throw;
            }
        }
    }
}
