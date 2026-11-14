using System.Text.Json;
using Inventory.Worker.Application;
using Inventory.Worker.Data;
using Inventory.Worker.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Contracts;
using Xunit;

namespace Inventory.Tests;

public sealed class ReserveStockHandlerTests
{
    private static (SqliteConnection Connection, InventoryDbContext Db) CreateDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new InventoryDbContext(options);
        db.Database.EnsureCreated();
        db.Stock.AddRange(
            new StockItem { Sku = "ABC-01", Disponible = 10 },
            new StockItem { Sku = "XYZ-20", Disponible = 0 });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return (connection, db);
    }

    [Fact]
    public async Task SameOrderCreatedHandledTwice_DiscountsOnceAndCreatesSingleOutbox()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;
        var handler = new ReserveStockHandler(db, NullLogger<ReserveStockHandler>.Instance);

        var evt = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), "ABC-01", 3, DateTimeOffset.UtcNow);

        var first = await handler.HandleAsync(evt);
        var second = await handler.HandleAsync(evt);

        Assert.Equal(ReserveStockOutcome.Reserved, first);
        Assert.Equal(ReserveStockOutcome.AlreadyProcessed, second);

        var item = await db.Stock.AsNoTracking().SingleAsync(s => s.Sku == "ABC-01");
        Assert.Equal(7, item.Disponible);

        var outbox = await db.OutboxMessages.AsNoTracking().ToListAsync();
        Assert.Single(outbox);
        Assert.Equal(MessagingConstants.StockReservedRoutingKey, outbox[0].RoutingKey);
    }

    [Fact]
    public async Task InsufficientStock_LeavesStockUnchangedAndEmitsRejected()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;
        var handler = new ReserveStockHandler(db, NullLogger<ReserveStockHandler>.Instance);

        var evt = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), "XYZ-20", 1, DateTimeOffset.UtcNow);

        var outcome = await handler.HandleAsync(evt);

        Assert.Equal(ReserveStockOutcome.Rejected, outcome);

        var item = await db.Stock.AsNoTracking().SingleAsync(s => s.Sku == "XYZ-20");
        Assert.Equal(0, item.Disponible);

        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(MessagingConstants.StockRejectedRoutingKey, outbox.RoutingKey);

        var rejected = JsonSerializer.Deserialize<StockRejected>(outbox.Payload, EventSerialization.Options);
        Assert.NotNull(rejected);
        Assert.Equal(RejectionReasons.InsufficientStock, rejected.Motivo);
    }

    [Fact]
    public async Task UnknownSku_EmitsStockRejectedWithUnknownSku()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;
        var handler = new ReserveStockHandler(db, NullLogger<ReserveStockHandler>.Instance);

        var evt = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(), "NO-SUCH-SKU", 2, DateTimeOffset.UtcNow);

        var outcome = await handler.HandleAsync(evt);

        Assert.Equal(ReserveStockOutcome.Rejected, outcome);

        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(MessagingConstants.StockRejectedRoutingKey, outbox.RoutingKey);

        var rejected = JsonSerializer.Deserialize<StockRejected>(outbox.Payload, EventSerialization.Options);
        Assert.NotNull(rejected);
        Assert.Equal(RejectionReasons.UnknownSku, rejected.Motivo);
    }
}
