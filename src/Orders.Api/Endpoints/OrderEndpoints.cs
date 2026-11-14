using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Orders.Api.Data;
using Orders.Api.Domain;

namespace Orders.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/orders", HandleCreateOrderAsync);
        app.MapGet("/orders", HandleListOrdersAsync);
        app.MapGet("/orders/{id:guid}", HandleGetOrderAsync);
        app.MapGet("/products", HandleListProductsAsync);
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/health/ready", HandleReadyAsync);
    }

    private static async Task<IResult> HandleCreateOrderAsync(
        CreateOrderRequest request,
        OrdersDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var trimmedSku = request.Sku?.Trim() ?? string.Empty;
        var skuExists = !string.IsNullOrEmpty(trimmedSku)
            && await db.Products.AnyAsync(p => p.Sku == trimmedSku, ct);

        var errors = OrderValidator.Validate(request, skuExists);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var logger = loggerFactory.CreateLogger("Orders.Api.Endpoints");
        var order = await CreateOrderWithOutboxAsync(db, request, logger, ct);

        return Results.Created($"/orders/{order.Id}", order);
    }

    public static async Task<Order> CreateOrderWithOutboxAsync(
        OrdersDbContext db,
        CreateOrderRequest request,
        ILogger logger,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var order = new Order
        {
            Id = orderId,
            ClienteNombre = request.ClienteNombre!.Trim(),
            Sku = request.Sku!.Trim(),
            Cantidad = request.Cantidad,
            Estado = OrderStatus.Pending,
            CreadoEn = now,
            ActualizadoEn = now,
            MotivoRechazo = null
        };

        var evt = new OrderCreated(
            EventId: eventId,
            OrderId: orderId,
            Sku: order.Sku,
            Cantidad: order.Cantidad,
            OcurridoEn: new DateTimeOffset(now, TimeSpan.Zero));

        var outbox = new OutboxMessage
        {
            Id = eventId,
            Type = nameof(OrderCreated),
            RoutingKey = MessagingConstants.OrderCreatedRoutingKey,
            Payload = JsonSerializer.Serialize(evt, EventSerialization.Options),
            CreatedAt = now
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Orders.Add(order);
        db.OutboxMessages.Add(outbox);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Created order {OrderId} (Pending) with outbox event {EventId} for SKU {Sku} x {Cantidad}",
            order.Id,
            eventId,
            order.Sku,
            order.Cantidad);

        return order;
    }

    private static async Task<IResult> HandleListOrdersAsync(OrdersDbContext db, CancellationToken ct)
    {
        var orders = await db.Orders
            .AsNoTracking()
            .OrderByDescending(o => o.CreadoEn)
            .ToListAsync(ct);

        return Results.Ok(orders);
    }

    private static async Task<IResult> HandleGetOrderAsync(Guid id, OrdersDbContext db, CancellationToken ct)
    {
        var order = await db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order is null ? Results.NotFound() : Results.Ok(order);
    }

    private static async Task<IResult> HandleListProductsAsync(OrdersDbContext db, CancellationToken ct)
    {
        var products = await db.Products
            .AsNoTracking()
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);

        return Results.Ok(products);
    }

    private static async Task<IResult> HandleReadyAsync(OrdersDbContext db, CancellationToken ct)
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
