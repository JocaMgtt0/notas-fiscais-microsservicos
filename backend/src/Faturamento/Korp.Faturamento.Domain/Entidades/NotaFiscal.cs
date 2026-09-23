using Korp.Faturamento.Domain.Excecoes;

namespace Korp.Faturamento.Domain.Entidades;

public enum StatusNotaFiscal
{
    Aberta = 1,

    /// <summary>
    /// Estado intermediario, criado por decisao de projeto. Existe porque a impressao atravessa dois servicos com bancos
    /// separados, e portanto nao cabe em uma transacao de banco.
    ///
    /// Enquanto a nota esta neste estado, ha uma operacao distribuida em curso.
    /// Sem ele nao daria para distinguir "ninguem imprimiu ainda" de "a baixa
    /// foi enviada e nao sabemos o desfecho", e a recuperacao de falha entre
    /// os servicos ficaria impossivel.
    /// </summary>
    EmProcessamento = 2,

    Fechada = 3
}

/// <summary>
/// Nota fiscal: raiz do agregado que controla seus itens e seu ciclo de vida.
///
/// A maquina de estados e o coracao desta entidade:
///
///     Aberta ---(IniciarProcessamento)--> EmProcessamento
///     EmProcessamento ---(ConfirmarImpressao)--> Fechada   [terminal]
///     EmProcessamento ---(ReverterParaAberta)--> Aberta    [compensacao]
///
/// Toda transicao invalida lanca <see cref="StatusInvalidoExcecao"/>. Nao
/// existe setter publico de status: quem quiser mudar o estado passa por um
/// dos metodos e obedece as regras.
/// </summary>
public class NotaFiscal
{
    private readonly List<ItemNotaFiscal> _itens = new();

    private NotaFiscal() { }

    private NotaFiscal(long numero)
    {
        Id = Guid.NewGuid();
        Numero = numero;
        Status = StatusNotaFiscal.Aberta;
        CriadaEm = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// RN04: sequencial e global. O valor vem de uma sequence do banco, nao de
    /// um MAX(numero) + 1, justamente para nao repetir numero sob concorrencia.
    /// </summary>
    public long Numero { get; private set; }

    public StatusNotaFiscal Status { get; private set; }
    public DateTime CriadaEm { get; private set; }
    public DateTime? FechadaEm { get; private set; }

    public IReadOnlyCollection<ItemNotaFiscal> Itens => _itens.AsReadOnly();

    public bool EstaAberta => Status == StatusNotaFiscal.Aberta;
    public bool EstaFechada => Status == StatusNotaFiscal.Fechada;

    public static NotaFiscal Criar(long numero)
    {
        if (numero <= 0)
            throw new DadosInvalidosExcecao("A numeracao da nota deve ser maior que zero.");

        return new NotaFiscal(numero);
    }

    // ---------- Itens (RN07) ----------

    /// <summary>
    /// Inclui um produto na nota. Se o produto ja estiver presente, soma a
    /// quantidade na linha existente em vez de criar uma segunda linha.
    ///
    /// Isso importa alem da estetica: duas linhas do mesmo produto poderiam
    /// passar pela validacao de saldo separadamente e estourar o estoque
    /// somadas.
    /// </summary>
    public ItemNotaFiscal AdicionarItem(
        Guid produtoId, string produtoCodigo, string produtoDescricao, int quantidade)
    {
        GarantirQuePodeSerEditada("adicionar item em");

        var existente = _itens.FirstOrDefault(i => i.ProdutoId == produtoId);

        if (existente is not null)
        {
            existente.SomarQuantidade(quantidade);
            return existente;
        }

        var item = new ItemNotaFiscal(Id, produtoId, produtoCodigo, produtoDescricao, quantidade);
        _itens.Add(item);
        return item;
    }

    public void AlterarQuantidadeItem(Guid itemId, int quantidade)
    {
        GarantirQuePodeSerEditada("alterar item de");

        var item = _itens.FirstOrDefault(i => i.Id == itemId)
                   ?? throw new ItemNaoEncontradoExcecao(itemId);

        item.AlterarQuantidade(quantidade);
    }

    public void RemoverItem(Guid itemId)
    {
        GarantirQuePodeSerEditada("remover item de");

        var item = _itens.FirstOrDefault(i => i.Id == itemId)
                   ?? throw new ItemNaoEncontradoExcecao(itemId);

        _itens.Remove(item);
    }

    // ---------- Maquina de estados ----------

    /// <summary>
    /// Primeiro passo da impressao: marca que existe uma operacao distribuida
    /// em curso. Gravado antes de chamar o servico de Estoque, para que uma
    /// queda no meio do caminho deixe rastro em vez de silencio.
    /// </summary>
    public void IniciarProcessamento()
    {
        if (Status != StatusNotaFiscal.Aberta)
            throw new StatusInvalidoExcecao("imprimir", Status.ToString());

        // RN08
        if (_itens.Count == 0)
            throw new NotaSemItensExcecao(Numero);

        Status = StatusNotaFiscal.EmProcessamento;
    }

    /// <summary>Baixa confirmada e PDF gerado: a nota se torna imutavel.</summary>
    public void ConfirmarImpressao()
    {
        if (Status != StatusNotaFiscal.EmProcessamento)
            throw new StatusInvalidoExcecao("confirmar a impressao de", Status.ToString());

        Status = StatusNotaFiscal.Fechada;
        FechadaEm = DateTime.UtcNow;
    }

    /// <summary>
    /// Compensacao: algo falhou durante a impressao e a nota volta a ser
    /// editavel. Usado tanto quando o Estoque recusa a baixa quanto quando
    /// ele esta fora do ar.
    /// </summary>
    public void ReverterParaAberta()
    {
        if (Status != StatusNotaFiscal.EmProcessamento)
            throw new StatusInvalidoExcecao("reverter", Status.ToString());

        Status = StatusNotaFiscal.Aberta;
    }

    // ---------- RN06 e RN07 ----------

    private void GarantirQuePodeSerEditada(string operacao)
    {
        if (Status != StatusNotaFiscal.Aberta)
            throw new StatusInvalidoExcecao(operacao, Status.ToString());
    }

    /// <summary>
    /// Guarda usada pelo caso de uso de exclusao. Nota Fechada e documento
    /// emitido: nao se apaga. Nota EmProcessamento tem operacao em curso.
    /// </summary>
    public void GarantirQuePodeSerExcluida() => GarantirQuePodeSerEditada("excluir");
}
