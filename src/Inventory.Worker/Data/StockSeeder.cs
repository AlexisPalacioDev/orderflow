using System.Text.Json;
using Inventory.Worker.Domain;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;

namespace Inventory.Worker.Data;

public static class StockSeeder
{
    private sealed record SeedEntry(string Sku, string Nombre, int Disponible);

    public static async Task SeedAsync(InventoryDbContext db, string seedPath, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Stock.AnyAsync(ct))
        {
            return;
        }

        if (!File.Exists(seedPath))
        {
            throw new FileNotFoundException($"Stock seed file not found at '{seedPath}'.", seedPath);
        }

        var json = await File.ReadAllTextAsync(seedPath, ct);
        var items = JsonSerializer.Deserialize<List<SeedEntry>>(json, EventSerialization.Options) ?? [];

        foreach (var item in items)
        {
            db.Stock.Add(new StockItem
            {
                Sku = item.Sku,
                Disponible = item.Disponible
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} stock items from {SeedPath}", items.Count, seedPath);
    }
}
