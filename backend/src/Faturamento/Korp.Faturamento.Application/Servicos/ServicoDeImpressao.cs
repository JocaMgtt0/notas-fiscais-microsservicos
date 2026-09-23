using Korp.Faturamento.Application.Contratos;
using Korp.Faturamento.Application.Dtos;
using Korp.Faturamento.Application.Excecoes;
using Korp.Faturamento.Domain.Entidades;
using Korp.Faturamento.Domain.Excecoes;
using Microsoft.Extensions.Logging;

namespace Korp.Faturamento.Application.Servicos;

/// <summary>
/// A operacao central do sistema: imprimir uma nota fiscal.
///
/// Ela atravessa dois servicos com bancos separados, e por isso **nao existe
/// transacao que cubra as duas pontas**. Nao da para fazer "ou tudo, ou nada"
/// com um BEGIN/COMMIT: o saldo esta em outro banco, em outro processo, do
/// outro lado da rede.
///
/// A saida e uma saga com compensacao. A sequencia e:
///
///   1. Valida a nota (Aberta, com itens) e grava EmProcessamento.
///      O commit acontece ANTES da chamada de rede, de proposito: se o
///      processo cair no passo seguinte, fica registrado que havia uma
///      operacao em curso, em vez de silencio.
///
///   2. Pede a baixa ao servico de Estoque.
///      Falhou por regra (sem saldo) ou por rede (servico fora do ar):
///      a nota volta para Aberta e o erro sobe. Nada foi alterado no estoque.
///
///   3. Gera o PDF.
///      Se falhar aqui, o saldo JA foi baixado. E o unico ponto onde existe
///      efeito a desfazer, e por isso o estorno e chamado: a compensacao.
///
///   4. Confirma: a nota vira Fechada e imutavel.
///
/// O caso em que ate o estorno falha esta tratado no passo 3 e resulta em
/// nota presa em EmProcessamento, sinalizando necessidade de intervencao.
/// Nenhuma compensacao e infalivel, e fingir o contrario seria pior do que
/// admitir o limite.
/// </summary>
public class ServicoDeImpressao
{
    private readonly IRepositorioDeNotas _repositorio;
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho;
    private readonly IServicoDeEstoque _estoque;
    private readonly IGeradorDePdf _geradorDePdf;
    private readonly ILogger<ServicoDeImpressao> _logger;

    public ServicoDeImpressao(
        IRepositorioDeNotas repositorio,
        IUnidadeDeTrabalho unidadeDeTrabalho,
        IServicoDeEstoque estoque,
        IGeradorDePdf geradorDePdf,
        ILogger<ServicoDeImpressao> logger)
    {
        _repositorio = repositorio;
        _unidadeDeTrabalho = unidadeDeTrabalho;
        _estoque = estoque;
        _geradorDePdf = geradorDePdf;
        _logger = logger;
    }

    public async Task<ResultadoImpressao> ImprimirAsync(Guid notaId, CancellationToken ct = default)
    {
        var nota = await _repositorio.ObterPorIdAsync(notaId, ct)
                   ?? throw new NotaNaoEncontradaExcecao(notaId);

        // Passo 1. Lanca StatusInvalidoExcecao se nao estiver Aberta (RN07)
        // e NotaSemItensExcecao se estiver vazia (RN08).
        nota.IniciarProcessamento();
        await _unidadeDeTrabalho.SalvarAsync(ct);

        _logger.LogInformation(
            "Nota {Numero} entrou em processamento com {Itens} itens.",
            nota.Numero, nota.Itens.Count);

        var itens = nota.Itens
            .Select(i => new ItemMovimentacao(i.ProdutoId, i.Quantidade))
            .ToList();

        // Passo 2.
        try
        {
            await _estoque.BaixarAsync(nota.Id, itens, ct);
        }
        catch (Exception excecao)
        {
            _logger.LogWarning(
                "Baixa recusada ou indisponivel para a nota {Numero}: {Motivo}. Revertendo para Aberta.",
                nota.Numero, excecao.Message);

            await ReverterAsync(nota, ct);
            throw;
        }

        _logger.LogInformation("Baixa confirmada para a nota {Numero}.", nota.Numero);

        // Passo 3.
        byte[] pdf;

        try
        {
            pdf = _geradorDePdf.Gerar(nota);
        }
        catch (Exception excecaoPdf)
        {
            _logger.LogError(excecaoPdf,
                "Falha ao gerar o PDF da nota {Numero} apos a baixa. Iniciando compensacao.",
                nota.Numero);

            try
            {
                await _estoque.EstornarAsync(nota.Id, itens, ct);
            }
            catch (Exception excecaoEstorno)
            {
                // Pior cenario: saldo baixado e compensacao falhou.
                // A nota permanece EmProcessamento como marcador de pendencia.
                _logger.LogCritical(excecaoEstorno,
                    "COMPENSACAO FALHOU para a nota {Numero}. O saldo foi baixado e nao foi " +
                    "devolvido. A nota permanece EmProcessamento para verificacao manual.",
                    nota.Numero);

                throw new IntervencaoManualNecessariaExcecao(nota.Id, nota.Numero);
            }

            _logger.LogInformation(
                "Compensacao concluida para a nota {Numero}. Saldo devolvido ao estoque.",
                nota.Numero);

            await ReverterAsync(nota, ct);
            throw new FalhaGeracaoPdfExcecao(excecaoPdf);
        }

        // Passo 4.
        nota.ConfirmarImpressao();
        await _unidadeDeTrabalho.SalvarAsync(ct);

        _logger.LogInformation("Nota {Numero} fechada com sucesso.", nota.Numero);

        return new ResultadoImpressao(nota.Id, nota.Numero, pdf);
    }

    /// <summary>
    /// Gera novamente o PDF de uma nota ja fechada, para download posterior.
    /// Nao regrava nada: a nota fechada e imutavel, entao o documento sai
    /// sempre identico. E por isso que nao precisamos armazenar o arquivo.
    /// </summary>
    public async Task<ResultadoImpressao> ObterPdfAsync(Guid notaId, CancellationToken ct = default)
    {
        var nota = await _repositorio.ObterPorIdAsync(notaId, ct)
                   ?? throw new NotaNaoEncontradaExcecao(notaId);

        if (nota.Status != StatusNotaFiscal.Fechada)
            throw new StatusInvalidoExcecao("baixar o PDF de", nota.Status.ToString());

        return new ResultadoImpressao(nota.Id, nota.Numero, _geradorDePdf.Gerar(nota));
    }

    private async Task ReverterAsync(NotaFiscal nota, CancellationToken ct)
    {
        nota.ReverterParaAberta();

        // CancellationToken.None de proposito: se o cliente desistiu da
        // requisicao, a reversao ainda precisa ser gravada. Deixar a nota
        // presa em EmProcessamento por causa de um cancelamento seria pior
        // do que gastar mais uma escrita.
        await _unidadeDeTrabalho.SalvarAsync(CancellationToken.None);
    }
}
