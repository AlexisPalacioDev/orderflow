using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
    [InlineData("", "ABC-01", 1, true, "clienteNombre")]
    [InlineData("Ana", "UNKNOWN", 1, false, "sku")]
    [InlineData("Ana", "ABC-01", 0, true, "cantidad")]
    public void OrderValidator_InvalidInput_ReportsExpectedFieldKey(
        string clienteNombre,
        string sku,
        int cantidad,
        bool skuExists,
        string expectedFieldKey)
    {
        var errors = OrderValidator.Validate(clienteNombre, sku, cantidad, skuExists);

        Assert.True(errors.ContainsKey(expectedFieldKey));
        Assert.Single(errors);
    }

    [Fact]
    public async Task CreateOrderEndpoint_RejectsUnknownSkuAndAcceptsSeededSku()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;

        var invalidResult = await OrderEndpoints.HandleCreateOrderAsync(
            new CreateOrderRequest("Ana", "NO-SUCH-SKU", 2),
            db,
            NullLoggerFactory.Instance);

        var badRequest = Assert.IsAssignableFrom<IStatusCodeHttpResult>(invalidResult);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);

        var validationValue = Assert.IsAssignableFrom<IValueHttpResult>(invalidResult).Value;
        var problem = Assert.IsType<HttpValidationProblemDetails>(validationValue);
        Assert.True(problem.Errors.ContainsKey("sku"));
        Assert.Equal(0, await db.Orders.CountAsync());

        var validResult = await OrderEndpoints.HandleCreateOrderAsync(
            new CreateOrderRequest("Ana", "ABC-01", 2),
            db,
            NullLoggerFactory.Instance);

        var created = Assert.IsAssignableFrom<IStatusCodeHttpResult>(validResult);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);

        var createdOrder = Assert.IsType<Order>(Assert.IsAssignableFrom<IValueHttpResult>(validResult).Value);
        Assert.Equal(OrderStatus.Pending, createdOrder.Estado);
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
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
    public async Task CreateOrderWithOutbox_PersistsPendingOrderAndOutboxMessage()
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

    [Fact]
    public async Task CreateOrderWithOutbox_WhenOutboxInsertFails_RollsBackOrder()
    {
        var (connection, db) = CreateDatabase();
        using var _ = connection;
        using var __ = db;

        await db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_outbox_insert BEFORE INSERT ON outbox_messages BEGIN SELECT RAISE(ABORT, 'simulated outbox failure'); END;");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            OrderEndpoints.CreateOrderWithOutboxAsync(
                db,
                new CreateOrderRequest("Carlos", "ABC-01", 4),
                NullLogger.Instance));

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Orders.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.OutboxMessages.AsNoTracking().CountAsync());
    }
}
