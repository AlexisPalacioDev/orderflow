using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Orders.Api.Domain;

namespace Orders.Api.Data;

public static class CatalogSeeder
{
    private sealed record SeedEntry(string Sku, string Nombre, int Disponible);

    public static async Task SeedAsync(OrdersDbContext db, string seedPath, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct))
        {
            return;
        }

        if (!File.Exists(seedPath))
        {
            throw new FileNotFoundException($"Catalog seed file not found at '{seedPath}'.", seedPath);
        }

        var json = await File.ReadAllTextAsync(seedPath, ct);
        var items = JsonSerializer.Deserialize<List<SeedEntry>>(json, EventSerialization.Options) ?? [];

        foreach (var item in items)
        {
            db.Products.Add(new Product
            {
                Sku = item.Sku,
                Nombre = item.Nombre
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} catalog products from {SeedPath}", items.Count, seedPath);
    }
}
