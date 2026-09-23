using FluentAssertions;
using Korp.Estoque.Domain.Entidades;
using Korp.Estoque.Domain.Excecoes;
using Xunit;

namespace Korp.Estoque.Tests.Dominio;

/// <summary>
/// Testes das regras de negocio do agregado Produto.
///
/// Nao tocam banco, HTTP nem container: exercitam apenas o dominio. Sao rapidos
/// e continuam valendo mesmo que a persistencia mude por completo. E esse
/// isolamento que a Clean Architecture compra.
/// </summary>
public class ProdutoTestes
{
    private static readonly Guid NotaQualquer = Guid.NewGuid();

    private static Produto ProdutoComSaldo(int saldo) =>
        Produto.Criar("PRD-001", "Teclado mecanico", saldo);

    // ---------- Criacao ----------

    [Fact]
    public void Criar_com_dados_validos_inicia_produto_consistente()
    {
        var produto = Produto.Criar("PRD-001", "Teclado mecanico", 10);

        produto.Id.Should().NotBeEmpty();
        produto.Codigo.Should().Be("PRD-001");
        produto.Descricao.Should().Be("Teclado mecanico");
        produto.Saldo.Should().Be(10);
        produto.Versao.Should().Be(1);
        produto.Movimentacoes.Should().BeEmpty();
    }

    [Fact]
    public void Criar_remove_espacos_em_volta_do_codigo_e_da_descricao()
    {
        var produto = Produto.Criar("  PRD-001  ", "  Teclado  ", 5);

        produto.Codigo.Should().Be("PRD-001");
        produto.Descricao.Should().Be("Teclado");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Criar_sem_codigo_e_rejeitado(string? codigo)
    {
        var acao = () => Produto.Criar(codigo!, "Teclado", 10);

        acao.Should().Throw<DadosInvalidosExcecao>()
            .Which.Codigo.Should().Be("DADOS_INVALIDOS");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Criar_sem_descricao_e_rejeitado(string? descricao)
    {
        var acao = () => Produto.Criar("PRD-001", descricao!, 10);

        acao.Should().Throw<DadosInvalidosExcecao>();
    }

    [Fact]
    public void Criar_com_saldo_negativo_e_rejeitado()
    {
        var acao = () => Produto.Criar("PRD-001", "Teclado", -1);

        acao.Should().Throw<DadosInvalidosExcecao>();
    }

    [Fact]
    public void Criar_com_codigo_longo_demais_e_rejeitado()
    {
        var codigoLongo = new string('X', Produto.TamanhoMaximoCodigo + 1);

        var acao = () => Produto.Criar(codigoLongo, "Teclado", 10);

        acao.Should().Throw<DadosInvalidosExcecao>();
    }

    // ---------- Baixa (RN02, RN03) ----------

    [Fact]
    public void Baixar_reduz_o_saldo_e_registra_a_movimentacao()
    {
        var produto = ProdutoComSaldo(10);

        // Exemplo canonico:
        // saldo anterior 10, nota usa 2, novo saldo 8.
        var movimentacao = produto.Baixar(2, NotaQualquer);

        produto.Saldo.Should().Be(8);
        produto.Movimentacoes.Should().ContainSingle();

        movimentacao.Tipo.Should().Be(TipoMovimentacao.Baixa);
        movimentacao.Quantidade.Should().Be(2);
        movimentacao.SaldoAnterior.Should().Be(10);
        movimentacao.SaldoPosterior.Should().Be(8);
        movimentacao.NotaId.Should().Be(NotaQualquer);
    }

    [Fact]
    public void Baixar_incrementa_a_versao_usada_no_controle_de_concorrencia()
    {
        var produto = ProdutoComSaldo(10);
        var versaoAnterior = produto.Versao;

        produto.Baixar(1, NotaQualquer);

        produto.Versao.Should().Be(versaoAnterior + 1);
    }

    [Fact]
    public void Baixar_o_saldo_inteiro_e_permitido_e_zera_o_estoque()
    {
        var produto = ProdutoComSaldo(3);

        produto.Baixar(3, NotaQualquer);

        produto.Saldo.Should().Be(0);
    }

    [Fact]
    public void Baixar_mais_do_que_o_saldo_e_rejeitado_e_nao_altera_nada()
    {
        var produto = ProdutoComSaldo(3);

        var acao = () => produto.Baixar(5, NotaQualquer);

        var excecao = acao.Should().Throw<SaldoInsuficienteExcecao>().Which;
        excecao.Codigo.Should().Be("SALDO_INSUFICIENTE");
        excecao.SaldoDisponivel.Should().Be(3);
        excecao.QuantidadeSolicitada.Should().Be(5);

        // O estado precisa permanecer intacto apos a recusa: e isso que
        // permite ao Faturamento devolver a nota para Aberta com seguranca.
        produto.Saldo.Should().Be(3);
        produto.Movimentacoes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Baixar_quantidade_nao_positiva_e_rejeitada(int quantidade)
    {
        var produto = ProdutoComSaldo(10);

        var acao = () => produto.Baixar(quantidade, NotaQualquer);

        acao.Should().Throw<QuantidadeInvalidaExcecao>()
            .Which.Codigo.Should().Be("QUANTIDADE_INVALIDA");
    }

    // ---------- Estorno (compensacao) ----------

    [Fact]
    public void Estornar_devolve_a_quantidade_ao_saldo()
    {
        var produto = ProdutoComSaldo(10);
        produto.Baixar(4, NotaQualquer);

        produto.Estornar(4, NotaQualquer);

        produto.Saldo.Should().Be(10);
        produto.Movimentacoes.Should().HaveCount(2);
        produto.Movimentacoes.Last().Tipo.Should().Be(TipoMovimentacao.Estorno);
    }

    [Fact]
    public void Baixa_seguida_de_estorno_deixa_o_saldo_como_estava()
    {
        var produto = ProdutoComSaldo(7);

        produto.Baixar(7, NotaQualquer);
        produto.Estornar(7, NotaQualquer);

        produto.Saldo.Should().Be(7);
    }

    // ---------- Alteracoes de cadastro ----------

    [Fact]
    public void AjustarSaldo_com_valor_negativo_e_rejeitado()
    {
        var produto = ProdutoComSaldo(10);

        var acao = () => produto.AjustarSaldo(-1);

        acao.Should().Throw<DadosInvalidosExcecao>();
        produto.Saldo.Should().Be(10);
    }

    [Fact]
    public void AlterarDescricao_atualiza_e_incrementa_a_versao()
    {
        var produto = ProdutoComSaldo(10);
        var versaoAnterior = produto.Versao;

        produto.AlterarDescricao("Teclado mecanico RGB");

        produto.Descricao.Should().Be("Teclado mecanico RGB");
        produto.Versao.Should().Be(versaoAnterior + 1);
    }

    [Fact]
    public void AlterarDescricao_para_vazio_e_rejeitado()
    {
        var produto = ProdutoComSaldo(10);

        var acao = () => produto.AlterarDescricao("   ");

        acao.Should().Throw<DadosInvalidosExcecao>();
        produto.Descricao.Should().Be("Teclado mecanico");
    }
}
