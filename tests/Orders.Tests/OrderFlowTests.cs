using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFlow.Contracts;
using Orders.Api.Data;
using Orders.Api.Domain;
using Orders.Api.Endpoints;
using Orders.Api.Messaging;
using Xunit;

namespace Orders.Tests;

public sealed class OrderFlowTests
{
    private static (SqliteConnection Connection, OrdersDbContext Db) CreateDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new OrdersDbContext(options);
        db.Database.EnsureCreated();
        db.Products.Add(new Product { Sku = "ABC-01", Nombre = "Teclado" });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return (connection, db);
    }

    [Theory]
    [InlineData("", "ABC-01", 1, true, false)]
    [InlineData("Ana", "UNKNOWN", 1, false, false)]
    [InlineData("Ana", "ABC-01", 0, true, false)]
    [InlineData("Ana", "ABC-01", 101, true, false)]
    [InlineData("Ana", "ABC-01", 5, true, true)]
    public void OrderValidator_ValidatesFieldsAsExpected(
        string clienteNombre,
        string sku,
        int cantidad,
        bool skuExists,
        bool expectedValid)
    {
        var errors = OrderValidator.Validate(new CreateOrderRequest(clienteNombre, sku, cantidad), skuExists);

        Assert.Equal(expectedValid, errors.Count == 0);
    }

    [Fact]
    public async Task StockResult_ConfirmsPendingOrderAndIgnoresContradictorySecondResult()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;
        var order = await OrderEndpoints.CreateOrderWithOutboxAsync(
            db,
            new CreateOrderRequest("Ana", "ABC-01", 2),
            NullLogger.Instance);

        var reserved = new StockReserved(Guid.NewGuid(), order.Id, "ABC-01", 2, DateTimeOffset.UtcNow);
        var reservedApplied = await StockResultConsumer.ApplyResultAsync(
            db,
            MessagingConstants.StockReservedRoutingKey,
            JsonSerializer.Serialize(reserved, EventSerialization.Options),
            NullLogger.Instance);

        var rejected = new StockRejected(Guid.NewGuid(), order.Id, "ABC-01", 2, RejectionReasons.InsufficientStock, DateTimeOffset.UtcNow);
        var rejectedApplied = await StockResultConsumer.ApplyResultAsync(
            db,
            MessagingConstants.StockRejectedRoutingKey,
            JsonSerializer.Serialize(rejected, EventSerialization.Options),
            NullLogger.Instance);

        Assert.True(reservedApplied);
        Assert.False(rejectedApplied);

        db.ChangeTracker.Clear();
        var reloaded = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(OrderStatus.Confirmed, reloaded.Estado);
        Assert.Null(reloaded.MotivoRechazo);
    }

    [Fact]
    public async Task CreateOrder_PersistsOrderAndOutboxInSingleTransaction()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;

        var order = await OrderEndpoints.CreateOrderWithOutboxAsync(
            db,
            new CreateOrderRequest("Carlos", "ABC-01", 4),
            NullLogger.Instance);

        var persistedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync();

        Assert.Equal(OrderStatus.Pending, persistedOrder.Estado);
        Assert.Equal(MessagingConstants.OrderCreatedRoutingKey, outbox.RoutingKey);
        Assert.Null(outbox.PublishedAt);

        var evt = JsonSerializer.Deserialize<OrderCreated>(outbox.Payload, EventSerialization.Options);
        Assert.NotNull(evt);
        Assert.Equal(order.Id, evt.OrderId);
        Assert.Equal("ABC-01", evt.Sku);
        Assert.Equal(4, evt.Cantidad);
    }
}
