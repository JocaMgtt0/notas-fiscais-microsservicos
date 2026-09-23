using Korp.Faturamento.Application.Contratos;
using Korp.Faturamento.Domain.Entidades;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Korp.Faturamento.Infrastructure.Pdf;

/// <summary>
/// Geracao do PDF da nota fiscal com QuestPDF.
///
/// O documento contem exatamente o que o modelo da nota define:
/// numeracao, status, data e a lista de produtos com quantidades. Nao ha
/// cliente, preco nem valor total porque estao fora do escopo do dominio.
///
/// O PDF e gerado sob demanda e nunca armazenado. Nota fechada e imutavel,
/// entao o documento sai identico toda vez: guardar o arquivo so acrescentaria
/// volume, risco de divergir do banco e um volume a mais no Docker.
/// </summary>
public class GeradorDePdfQuestPdf : IGeradorDePdf
{
    static GeradorDePdfQuestPdf()
    {
        // Licenca Community: gratuita para uso nas condicoes da propria
        // QuestPDF. Precisa ser definida uma vez antes do primeiro documento.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Gerar(NotaFiscal nota)
    {
        var documento = Document.Create(container =>
        {
            container.Page(pagina =>
            {
                pagina.Size(PageSizes.A4);
                pagina.Margin(2, Unit.Centimetre);
                pagina.DefaultTextStyle(x => x.FontSize(10).FontFamily(Fonts.Arial));

                pagina.Header().Element(c => MontarCabecalho(c, nota));
                pagina.Content().Element(c => MontarConteudo(c, nota));
                pagina.Footer().Element(MontarRodape);
            });
        });

        return documento.GeneratePdf();
    }

    private static void MontarCabecalho(IContainer container, NotaFiscal nota)
    {
        container.Column(coluna =>
        {
            coluna.Item().Row(linha =>
            {
                linha.RelativeItem().Column(esquerda =>
                {
                    esquerda.Item().Text("NOTA FISCAL").FontSize(20).Bold();
                    esquerda.Item().Text($"Numero {nota.Numero:D6}").FontSize(12).SemiBold();
                });

                linha.ConstantItem(160).Column(direita =>
                {
                    direita.Item().AlignRight().Text($"Status: {nota.Status}").SemiBold();

                    direita.Item().AlignRight()
                        .Text($"Emissao: {(nota.FechadaEm ?? nota.CriadaEm):dd/MM/yyyy HH:mm}");

                    direita.Item().AlignRight()
                        .Text($"Criada em: {nota.CriadaEm:dd/MM/yyyy HH:mm}")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });

            coluna.Item().PaddingVertical(8).LineHorizontal(1).LineColor(Colors.Grey.Medium);
        });
    }

    private static void MontarConteudo(IContainer container, NotaFiscal nota)
    {
        container.PaddingTop(6).Column(coluna =>
        {
            coluna.Item().Text("Produtos").FontSize(12).Bold();
            coluna.Item().PaddingTop(6).Table(tabela =>
            {
                tabela.ColumnsDefinition(colunas =>
                {
                    colunas.ConstantColumn(90);   // codigo
                    colunas.RelativeColumn();     // descricao
                    colunas.ConstantColumn(80);   // quantidade
                });

                tabela.Header(cabecalho =>
                {
                    cabecalho.Cell().Element(CelulaDeCabecalho).Text("Codigo");
                    cabecalho.Cell().Element(CelulaDeCabecalho).Text("Descricao");
                    cabecalho.Cell().Element(CelulaDeCabecalho).AlignRight().Text("Quantidade");
                });

                foreach (var item in nota.Itens.OrderBy(i => i.ProdutoCodigo))
                {
                    tabela.Cell().Element(Celula).Text(item.ProdutoCodigo);
                    tabela.Cell().Element(Celula).Text(item.ProdutoDescricao);
                    tabela.Cell().Element(Celula).AlignRight().Text(item.Quantidade.ToString());
                }
            });

            coluna.Item().PaddingTop(12).AlignRight().Column(totais =>
            {
                totais.Item().Text($"Itens distintos: {nota.Itens.Count}").SemiBold();
                totais.Item().Text($"Quantidade total: {nota.Itens.Sum(i => i.Quantidade)}").SemiBold();
            });
        });

        static IContainer CelulaDeCabecalho(IContainer c) => c
            .Background(Colors.Grey.Lighten3)
            .BorderBottom(1).BorderColor(Colors.Grey.Medium)
            .PaddingVertical(5).PaddingHorizontal(4)
            .DefaultTextStyle(x => x.SemiBold());

        static IContainer Celula(IContainer c) => c
            .BorderBottom(1).BorderColor(Colors.Grey.Lighten2)
            .PaddingVertical(5).PaddingHorizontal(4);
    }

    private static void MontarRodape(IContainer container)
    {
        container.Column(coluna =>
        {
            coluna.Item().PaddingBottom(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

            coluna.Item().Row(linha =>
            {
                linha.RelativeItem()
                    .Text("Documento gerado pelo sistema de emissao de notas fiscais.")
                    .FontSize(8).FontColor(Colors.Grey.Darken1);

                linha.ConstantItem(100).AlignRight().Text(texto =>
                {
                    texto.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                    texto.Span("Pagina ");
                    texto.CurrentPageNumber();
                    texto.Span(" de ");
                    texto.TotalPages();
                });
            });
        });
    }
}
