using FluentAssertions;
using Korp.Faturamento.Domain.Entidades;
using Korp.Faturamento.Domain.Excecoes;
using Xunit;

namespace Korp.Faturamento.Tests.Dominio;

/// <summary>
/// Testes da maquina de estados e das regras de edicao da nota fiscal.
///
/// A maquina de estados e o ponto mais delicado do sistema: e ela que
/// sustenta a recuperacao de falha entre os dois servicos. Cada transicao
/// valida e cada transicao proibida tem teste.
/// </summary>
public class NotaFiscalTestes
{
    private static readonly Guid Teclado = Guid.NewGuid();
    private static readonly Guid Mouse = Guid.NewGuid();

    private static NotaFiscal NotaAbertaComItem(int quantidade = 2)
    {
        var nota = NotaFiscal.Criar(1);
        nota.AdicionarItem(Teclado, "PRD-001", "Teclado mecanico", quantidade);
        return nota;
    }

    private static NotaFiscal NotaFechada()
    {
        var nota = NotaAbertaComItem();
        nota.IniciarProcessamento();
        nota.ConfirmarImpressao();
        return nota;
    }

    // ---------- Criacao ----------

    [Fact]
    public void Nota_nasce_aberta_e_sem_itens()
    {
        var nota = NotaFiscal.Criar(42);

        nota.Numero.Should().Be(42);
        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
        nota.EstaAberta.Should().BeTrue();
        nota.EstaFechada.Should().BeFalse();
        nota.Itens.Should().BeEmpty();
        nota.FechadaEm.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Nota_com_numeracao_nao_positiva_e_rejeitada(long numero)
    {
        var acao = () => NotaFiscal.Criar(numero);

        acao.Should().Throw<DadosInvalidosExcecao>();
    }

    // ---------- Itens ----------

    [Fact]
    public void AdicionarItem_grava_o_snapshot_do_produto()
    {
        var nota = NotaFiscal.Criar(1);

        var item = nota.AdicionarItem(Teclado, "PRD-001", "Teclado mecanico", 3);

        // RN11: o item guarda copia de codigo e descricao, e nao apenas o id.
        // E o que permite imprimir a nota com o servico de Estoque fora do ar.
        item.ProdutoCodigo.Should().Be("PRD-001");
        item.ProdutoDescricao.Should().Be("Teclado mecanico");
        item.Quantidade.Should().Be(3);
        nota.Itens.Should().ContainSingle();
    }

    [Fact]
    public void AdicionarItem_do_mesmo_produto_soma_na_linha_existente()
    {
        var nota = NotaFiscal.Criar(1);

        nota.AdicionarItem(Teclado, "PRD-001", "Teclado", 2);
        nota.AdicionarItem(Teclado, "PRD-001", "Teclado", 3);

        // Duas linhas do mesmo produto passariam pela validacao de saldo
        // separadamente e estourariam o estoque somadas.
        nota.Itens.Should().ContainSingle();
        nota.Itens.Single().Quantidade.Should().Be(5);
    }

    [Fact]
    public void AdicionarItem_de_produtos_diferentes_cria_linhas_separadas()
    {
        var nota = NotaFiscal.Criar(1);

        nota.AdicionarItem(Teclado, "PRD-001", "Teclado", 2);
        nota.AdicionarItem(Mouse, "PRD-002", "Mouse", 1);

        nota.Itens.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AdicionarItem_com_quantidade_nao_positiva_e_rejeitado(int quantidade)
    {
        var nota = NotaFiscal.Criar(1);

        var acao = () => nota.AdicionarItem(Teclado, "PRD-001", "Teclado", quantidade);

        acao.Should().Throw<QuantidadeInvalidaExcecao>();
    }

    [Fact]
    public void RemoverItem_tira_a_linha_da_nota()
    {
        var nota = NotaAbertaComItem();
        var itemId = nota.Itens.Single().Id;

        nota.RemoverItem(itemId);

        nota.Itens.Should().BeEmpty();
    }

    [Fact]
    public void RemoverItem_inexistente_e_rejeitado()
    {
        var nota = NotaAbertaComItem();

        var acao = () => nota.RemoverItem(Guid.NewGuid());

        acao.Should().Throw<ItemNaoEncontradoExcecao>();
    }

    [Fact]
    public void AlterarQuantidadeItem_atualiza_a_linha()
    {
        var nota = NotaAbertaComItem(2);
        var itemId = nota.Itens.Single().Id;

        nota.AlterarQuantidadeItem(itemId, 7);

        nota.Itens.Single().Quantidade.Should().Be(7);
    }

    // ---------- RN06: nota fechada e imutavel ----------

    [Fact]
    public void Nota_fechada_nao_aceita_novo_item()
    {
        var nota = NotaFechada();

        var acao = () => nota.AdicionarItem(Mouse, "PRD-002", "Mouse", 1);

        acao.Should().Throw<StatusInvalidoExcecao>()
            .Which.Codigo.Should().Be("NOTA_STATUS_INVALIDO");
    }

    [Fact]
    public void Nota_fechada_nao_aceita_remocao_de_item()
    {
        var nota = NotaFechada();
        var itemId = nota.Itens.First().Id;

        var acao = () => nota.RemoverItem(itemId);

        acao.Should().Throw<StatusInvalidoExcecao>();
    }

    [Fact]
    public void Nota_fechada_nao_pode_ser_excluida()
    {
        var nota = NotaFechada();

        var acao = () => nota.GarantirQuePodeSerExcluida();

        acao.Should().Throw<StatusInvalidoExcecao>();
    }

    // ---------- Maquina de estados ----------

    [Fact]
    public void IniciarProcessamento_move_a_nota_de_aberta_para_em_processamento()
    {
        var nota = NotaAbertaComItem();

        nota.IniciarProcessamento();

        nota.Status.Should().Be(StatusNotaFiscal.EmProcessamento);
    }

    [Fact]
    public void IniciarProcessamento_sem_itens_e_rejeitado()
    {
        var nota = NotaFiscal.Criar(1);

        var acao = () => nota.IniciarProcessamento();

        acao.Should().Throw<NotaSemItensExcecao>()
            .Which.Codigo.Should().Be("NOTA_SEM_ITENS");
    }

    [Fact]
    public void Nota_fechada_nao_pode_ser_impressa_de_novo()
    {
        var nota = NotaFechada();

        var acao = () => nota.IniciarProcessamento();

        acao.Should().Throw<StatusInvalidoExcecao>()
            .Which.StatusAtual.Should().Be("Fechada");
    }

    [Fact]
    public void Nota_em_processamento_nao_pode_iniciar_processamento_de_novo()
    {
        var nota = NotaAbertaComItem();
        nota.IniciarProcessamento();

        var acao = () => nota.IniciarProcessamento();

        acao.Should().Throw<StatusInvalidoExcecao>();
    }

    [Fact]
    public void ConfirmarImpressao_fecha_a_nota_e_marca_a_data()
    {
        var nota = NotaAbertaComItem();
        nota.IniciarProcessamento();

        nota.ConfirmarImpressao();

        nota.Status.Should().Be(StatusNotaFiscal.Fechada);
        nota.EstaFechada.Should().BeTrue();
        nota.FechadaEm.Should().NotBeNull();
        nota.FechadaEm.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ConfirmarImpressao_direto_de_aberta_e_rejeitado()
    {
        var nota = NotaAbertaComItem();

        // Sem passar por EmProcessamento nao ha baixa de estoque confirmada.
        // Permitir este atalho abriria caminho para fechar nota sem baixar saldo.
        var acao = () => nota.ConfirmarImpressao();

        acao.Should().Throw<StatusInvalidoExcecao>();
    }

    [Fact]
    public void ReverterParaAberta_desfaz_o_processamento()
    {
        var nota = NotaAbertaComItem();
        nota.IniciarProcessamento();

        nota.ReverterParaAberta();

        nota.Status.Should().Be(StatusNotaFiscal.Aberta);
        nota.FechadaEm.Should().BeNull();
    }

    [Fact]
    public void Nota_revertida_volta_a_aceitar_edicao()
    {
        var nota = NotaAbertaComItem();
        nota.IniciarProcessamento();
        nota.ReverterParaAberta();

        var acao = () => nota.AdicionarItem(Mouse, "PRD-002", "Mouse", 1);

        acao.Should().NotThrow();
        nota.Itens.Should().HaveCount(2);
    }

    [Fact]
    public void ReverterParaAberta_de_nota_fechada_e_rejeitado()
    {
        var nota = NotaFechada();

        // Nota fechada e terminal: reverter apagaria um documento ja emitido.
        var acao = () => nota.ReverterParaAberta();

        acao.Should().Throw<StatusInvalidoExcecao>();
    }
}
