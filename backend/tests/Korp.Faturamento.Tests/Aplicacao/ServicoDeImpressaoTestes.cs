using FluentAssertions;
using Korp.Faturamento.Application.Contratos;
using Korp.Faturamento.Application.Dtos;
using Korp.Faturamento.Application.Excecoes;
using Korp.Faturamento.Application.Servicos;
using Korp.Faturamento.Domain.Entidades;
using Korp.Faturamento.Domain.Excecoes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Korp.Faturamento.Tests.Aplicacao;

/// <summary>
/// Testes do caso de uso de impressao, que e a operacao distribuida do sistema.
///
/// Todos os caminhos de falha sao exercitados aqui com dubles, sem subir banco
/// nem servidor. E isso que a inversao de dependencia compra: o cenario
/// "servico de Estoque fora do ar" vira uma linha de configuracao de mock, em
/// vez de exigir derrubar um container.
/// </summary>
public class ServicoDeImpressaoTestes
{
    private readonly IRepositorioDeNotas _repositorio = Substitute.For<IRepositorioDeNotas>();
    private readonly IUnidadeDeTrabalho _unidadeDeTrabalho = Substitute.For<IUnidadeDeTrabalho>();
    private readonly IServicoDeEstoque _estoque = Substitute.For<IServicoDeEstoque>();
    private readonly IGeradorDePdf _geradorDePdf = Substitute.For<IGeradorDePdf>();

    private readonly ServicoDeImpressao _servico;

    private static readonly byte[] PdfFalso = { 0x25, 0x50, 0x44, 0x46 };

    public ServicoDeImpressaoTestes()
    {
        _servico = new ServicoDeImpressao(
            _repositorio, _unidadeDeTrabalho, _estoque, _geradorDePdf,
            NullLogger<ServicoDeImpressao>.Instance);
    }

    private NotaFiscal RegistrarNotaAbertaComItem()
    {
        var nota = NotaFiscal.Criar(1);
        nota.AdicionarItem(Guid.NewGuid(), "PRD-001", "Teclado mecanico", 2);

        _repositorio.ObterPorIdAsync(nota.Id, Arg.Any<CancellationToken>()).Returns(nota);
        _geradorDePdf.Gerar(Arg.Any<NotaFiscal>()).Returns(PdfFalso);

        return nota;
    }

    // ---------- Caminho feliz ----------

    [Fact]
    public async Task Impressao_bem_sucedida_baixa_o_estoque_fecha_a_nota_e_devolve_o_pdf()
    {
        var nota = RegistrarNotaAbertaComItem();

        var resultado = await _servico.ImprimirAsync(nota.Id);

        nota.Status.Should().Be(StatusNotaFiscal.Fechada);
        nota.FechadaEm.Should().NotBeNull();
        resultado.Pdf.Should().BeEquivalentTo(PdfFalso);
        resultado.Numero.Should().Be(nota.Numero);

        await _estoque.Received(1).BaixarAsync(
            nota.Id, Arg.Any<IReadOnlyList<ItemMovimentacao>>(), Arg.Any<CancellationToken>());

        // Caminho feliz nao compensa nada.
        await _estoque.DidNotReceive().EstornarAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ItemMovimentacao>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Impressao_envia_ao_estoque_exatamente_os_itens_da_nota()
    {
        var nota = NotaFiscal.Criar(1);
        var produtoA = Guid.NewGuid();
        var produtoB = Guid.NewGuid();
        nota.AdicionarItem(produtoA, "PRD-001", "Teclado", 2);
        nota.AdicionarItem(produtoB, "PRD-002", "Mouse", 5);

        _repositorio.ObterPorIdAsync(nota.Id, Arg.Any<CancellationToken>()).Returns(nota);
        _geradorDePdf.Gerar(Arg.Any<NotaFiscal>()).Returns(PdfFalso);

        await _servico.ImprimirAsync(nota.Id);

        await _estoque.Received(1).BaixarAsync(
            nota.Id,
            Arg.Is<IReadOnlyList<ItemMovimentacao>>(itens =>
                itens.Count == 2 &&
                itens.Any(i => i.ProdutoId == produtoA && i.Quantidade == 2) &&
                itens.Any(i => i.ProdutoId == produtoB && i.Quantidade == 5)),
            Arg.Any<CancellationToken>());
    }

    // ---------- Guardas antes de tocar a rede ----------

    [Fact]
    public async Task Nota_inexistente_nao_chama_o_estoque()
    {
        _repositorio.ObterPorIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((NotaFiscal?)null);

        var acao = async () => await _servico.ImprimirAsync(Guid.NewGuid());

        await acao.Should().ThrowAsync<NotaNaoEncontradaExcecao>();
        await _estoque.DidNotReceiveWithAnyArgs().BaixarAsync(default, default!, default);
    }

    [Fact]
    public async Task Nota_ja_fechada_nao_pode_ser_impressa_de_novo()
    {
        var nota = RegistrarNotaAbertaComItem();
        nota.IniciarProcessamento();
        nota.ConfirmarImpressao();

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        await acao.Should().ThrowAsync<StatusInvalidoExcecao>();
        await _estoque.DidNotReceiveWithAnyArgs().BaixarAsync(default, default!, default);
    }

    [Fact]
    public async Task Nota_sem_itens_nao_chama_o_estoque()
    {
        var nota = NotaFiscal.Criar(1);
        _repositorio.ObterPorIdAsync(nota.Id, Arg.Any<CancellationToken>()).Returns(nota);

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        await acao.Should().ThrowAsync<NotaSemItensExcecao>();
        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
        await _estoque.DidNotReceiveWithAnyArgs().BaixarAsync(default, default!, default);
    }

    // ---------- Falha na baixa: recuperacao entre servicos ----------

    [Fact]
    public async Task Estoque_fora_do_ar_devolve_a_nota_para_aberta_e_nao_compensa()
    {
        var nota = RegistrarNotaAbertaComItem();

        _estoque.BaixarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ItemMovimentacao>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new EstoqueIndisponivelExcecao());

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        await acao.Should().ThrowAsync<EstoqueIndisponivelExcecao>();

        // O ponto central do requisito de tratamento de falhas: o sistema
        // volta a um estado consistente e o usuario pode tentar de novo.
        nota.Status.Should().Be(StatusNotaFiscal.Aberta);

        // Nao houve baixa, entao nao ha o que estornar. Chamar estorno aqui
        // devolveria saldo que nunca saiu, inflando o estoque.
        await _estoque.DidNotReceiveWithAnyArgs().EstornarAsync(default, default!, default);
    }

    [Fact]
    public async Task Saldo_insuficiente_devolve_a_nota_para_aberta()
    {
        var nota = RegistrarNotaAbertaComItem();

        _estoque.BaixarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ItemMovimentacao>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new SaldoInsuficienteNoEstoqueExcecao(
                new[] { new FaltaDeSaldo("PRD-001", 1, 2) }));

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        var excecao = (await acao.Should().ThrowAsync<SaldoInsuficienteNoEstoqueExcecao>()).Which;
        excecao.Faltas.Should().ContainSingle()
            .Which.ProdutoCodigo.Should().Be("PRD-001");

        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
    }

    [Fact]
    public async Task Conflito_de_concorrencia_devolve_a_nota_para_aberta()
    {
        var nota = RegistrarNotaAbertaComItem();

        _estoque.BaixarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ItemMovimentacao>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConflitoDeConcorrenciaExcecao());

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        await acao.Should().ThrowAsync<ConflitoDeConcorrenciaExcecao>();
        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
    }

    // ---------- Falha no PDF: unico ponto com efeito a desfazer ----------

    [Fact]
    public async Task Falha_ao_gerar_o_pdf_estorna_a_baixa_e_devolve_a_nota_para_aberta()
    {
        var nota = RegistrarNotaAbertaComItem();

        _geradorDePdf.Gerar(Arg.Any<NotaFiscal>())
            .Throws(new InvalidOperationException("fonte indisponivel"));

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        await acao.Should().ThrowAsync<FalhaGeracaoPdfExcecao>();

        // A baixa ja tinha sido confirmada, entao aqui a compensacao e obrigatoria.
        await _estoque.Received(1).EstornarAsync(
            nota.Id, Arg.Any<IReadOnlyList<ItemMovimentacao>>(), Arg.Any<CancellationToken>());

        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
    }

    [Fact]
    public async Task Quando_ate_o_estorno_falha_a_nota_fica_em_processamento_para_analise()
    {
        var nota = RegistrarNotaAbertaComItem();

        _geradorDePdf.Gerar(Arg.Any<NotaFiscal>())
            .Throws(new InvalidOperationException("fonte indisponivel"));

        _estoque.EstornarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<ItemMovimentacao>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new EstoqueIndisponivelExcecao());

        var acao = async () => await _servico.ImprimirAsync(nota.Id);

        var excecao = (await acao.Should().ThrowAsync<IntervencaoManualNecessariaExcecao>()).Which;
        excecao.Numero.Should().Be(nota.Numero);

        // Nenhuma compensacao e infalivel. Neste caso o saldo saiu e nao voltou,
        // e a nota permanece EmProcessamento sinalizando pendencia humana.
        // Devolve-la para Aberta esconderia a inconsistencia.
        nota.Status.Should().Be(StatusNotaFiscal.EmProcessamento);
    }

    // ---------- Download posterior do PDF ----------

    [Fact]
    public async Task ObterPdf_de_nota_fechada_gera_o_documento_novamente()
    {
        var nota = RegistrarNotaAbertaComItem();
        await _servico.ImprimirAsync(nota.Id);

        var resultado = await _servico.ObterPdfAsync(nota.Id);

        resultado.Pdf.Should().BeEquivalentTo(PdfFalso);
        nota.Status.Should().Be(StatusNotaFiscal.Fechada);
    }

    [Fact]
    public async Task ObterPdf_de_nota_aberta_e_rejeitado()
    {
        var nota = RegistrarNotaAbertaComItem();

        var acao = async () => await _servico.ObterPdfAsync(nota.Id);

        await acao.Should().ThrowAsync<StatusInvalidoExcecao>();
    }
}
