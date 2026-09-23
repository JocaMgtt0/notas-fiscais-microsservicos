using Korp.Estoque.Domain.Entidades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Korp.Estoque.Infrastructure.Persistencia;

/// <summary>
/// Popula o catalogo na primeira subida, para a demonstracao nao comecar
/// com a tela vazia.
///
/// Nao usa HasData do EF de proposito: HasData exige identificadores fixos
/// no modelo e nao consegue passar pelo construtor privado da entidade, o que
/// obrigaria a afrouxar o encapsulamento do dominio so para semear dados.
/// </summary>
public static class SemeadorDeDados
{
    public static async Task SemearAsync(EstoqueDbContext contexto, ILogger logger, CancellationToken ct = default)
    {
        if (await contexto.Produtos.AnyAsync(ct))
        {
            logger.LogInformation("Catalogo ja possui produtos. Semeadura ignorada.");
            return;
        }

        var produtos = new[]
        {
            Produto.Criar("PRD-001", "Teclado mecanico ABNT2",           10),
            Produto.Criar("PRD-002", "Mouse optico sem fio",             25),
            Produto.Criar("PRD-003", "Monitor 24 polegadas Full HD",      7),
            Produto.Criar("PRD-004", "Headset com microfone",            15),
            Produto.Criar("PRD-005", "Webcam 1080p",                     12),
            Produto.Criar("PRD-006", "Cabo HDMI 2 metros",               50),
            Produto.Criar("PRD-007", "Hub USB-C 7 portas",                8),
            Produto.Criar("PRD-008", "SSD NVMe 1TB",                      4),

            // Saldo 1 de proposito: e este o produto usado para demonstrar
            // o tratamento de concorrencia. Duas notas disputando a ultima
            // unidade, uma fecha e a outra recebe erro.
            Produto.Criar("PRD-009", "Placa de video (ultima unidade)",   1),

            // Saldo 0 de proposito: demonstra a recusa por saldo insuficiente
            // sem precisar esvaziar nenhum outro produto durante a demonstracao.
            Produto.Criar("PRD-010", "Notebook 16GB (sem estoque)",       0)
        };

        contexto.Produtos.AddRange(produtos);
        await contexto.SaveChangesAsync(ct);

        logger.LogInformation("Catalogo semeado com {Total} produtos.", produtos.Length);
    }
}
