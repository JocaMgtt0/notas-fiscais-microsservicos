using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Korp.Estoque.Application.Dtos;
using Xunit;

namespace Korp.Estoque.Tests.Integracao;

/// <summary>
/// Cenario de concorrencia: produto com saldo 1 sendo utilizado
/// simultaneamente por duas notas.
///
/// Este teste so tem valor contra um banco real. Com duble, ele provaria
/// apenas que o codigo chama os metodos na ordem certa. O que precisa ser
/// demonstrado e outra coisa: que duas transacoes concorrentes disputando a
/// MESMA LINHA terminam de forma consistente, e isso quem decide e o
/// PostgreSQL, nao o C#.
///
/// O mecanismo sob teste: a coluna "versao" do produto e token de concorrencia.
/// O EF inclui o valor original dela no WHERE do UPDATE. A transacao que
/// chegar depois nao encontra a linha com a versao que leu, afeta zero linhas
/// e recebe DbUpdateConcurrencyException, que dispara nova tentativa. Na nova
/// tentativa o saldo ja e zero, e a propria entidade recusa a baixa.
/// </summary>
public class ConcorrenciaIntegracaoTestes : IClassFixture<FabricaDeApiDeEstoque>
{
    private readonly FabricaDeApiDeEstoque _fabrica;
    private readonly HttpClient _cliente;

    public ConcorrenciaIntegracaoTestes(FabricaDeApiDeEstoque fabrica)
    {
        _fabrica = fabrica;
        _cliente = fabrica.CreateClient();
    }

    private async Task<ProdutoDto> CriarProdutoAsync(int saldo)
    {
        var dto = new CriarProdutoDto(
            $"CNC-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            "Ultima unidade em disputa",
            saldo);

        var resposta = await _cliente.PostAsJsonAsync("/api/produtos", dto);
        return (await resposta.Content.ReadFromJsonAsync<ProdutoDto>())!;
    }

    private async Task<int> SaldoAtualAsync(Guid id) =>
        (await _cliente.GetFromJsonAsync<ProdutoDto>($"/api/produtos/{id}"))!.Saldo;

    [Fact]
    public async Task Duas_notas_disputando_a_ultima_unidade_apenas_uma_vence()
    {
        var produto = await CriarProdutoAsync(saldo: 1);

        // Clientes distintos para que as requisicoes nao compartilhem conexao
        // e cheguem de fato em paralelo ao servidor.
        var primeira = _fabrica.CreateClient();
        var segunda = _fabrica.CreateClient();

        var corpoA = new MovimentacaoEstoqueDto(
            Guid.NewGuid(), new[] { new ItemMovimentacaoDto(produto.Id, 1) });

        var corpoB = new MovimentacaoEstoqueDto(
            Guid.NewGuid(), new[] { new ItemMovimentacaoDto(produto.Id, 1) });

        var respostas = await Task.WhenAll(
            primeira.PostAsJsonAsync("/api/produtos/baixa", corpoA),
            segunda.PostAsJsonAsync("/api/produtos/baixa", corpoB));

        var vitoriosas = respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        var recusadas = respostas.Count(r =>
            r.StatusCode == HttpStatusCode.UnprocessableEntity ||
            r.StatusCode == HttpStatusCode.Conflict);

        // O coracao do teste: exatamente uma passa.
        vitoriosas.Should().Be(1, "somente uma nota pode consumir a ultima unidade");
        recusadas.Should().Be(1, "a outra precisa receber recusa explicita, nao silencio");

        // E o mais importante: o estoque nao fica negativo nem perde unidade.
        (await SaldoAtualAsync(produto.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Muitas_baixas_simultaneas_nunca_deixam_o_saldo_negativo(int concorrentes)
    {
        // Saldo permite metade das tentativas. As demais precisam ser recusadas.
        var disponivel = concorrentes / 2;
        var produto = await CriarProdutoAsync(saldo: disponivel);

        var tarefas = Enumerable.Range(0, concorrentes).Select(_ =>
        {
            var cliente = _fabrica.CreateClient();
            var corpo = new MovimentacaoEstoqueDto(
                Guid.NewGuid(), new[] { new ItemMovimentacaoDto(produto.Id, 1) });

            return cliente.PostAsJsonAsync("/api/produtos/baixa", corpo);
        });

        var respostas = await Task.WhenAll(tarefas);

        var sucessos = respostas.Count(r => r.StatusCode == HttpStatusCode.NoContent);

        sucessos.Should().Be(disponivel,
            "o numero de baixas efetivadas precisa ser exatamente o saldo disponivel");

        // A invariante que realmente importa. Um sistema sem controle de
        // concorrencia passaria nas duas asserçoes anteriores por acaso, mas
        // falharia aqui com saldo negativo ou com unidades perdidas.
        (await SaldoAtualAsync(produto.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Baixa_e_estorno_simultaneos_preservam_o_saldo_total()
    {
        var produto = await CriarProdutoAsync(saldo: 10);
        var nota = Guid.NewGuid();
        var itens = new[] { new ItemMovimentacaoDto(produto.Id, 3) };

        // Baixa inicial, para haver o que estornar.
        await _cliente.PostAsJsonAsync("/api/produtos/baixa",
            new MovimentacaoEstoqueDto(nota, itens));

        var clienteBaixa = _fabrica.CreateClient();
        var clienteEstorno = _fabrica.CreateClient();

        await Task.WhenAll(
            clienteBaixa.PostAsJsonAsync("/api/produtos/baixa",
                new MovimentacaoEstoqueDto(Guid.NewGuid(), itens)),
            clienteEstorno.PostAsJsonAsync("/api/produtos/estorno",
                new MovimentacaoEstoqueDto(nota, itens)));

        // 10 - 3 (inicial) - 3 (baixa) + 3 (estorno) = 7.
        // Sem controle de concorrencia, uma das operacoes sobrescreveria a
        // outra e o resultado seria 4 ou 10, nunca 7.
        (await SaldoAtualAsync(produto.Id)).Should().Be(7);
    }
}
