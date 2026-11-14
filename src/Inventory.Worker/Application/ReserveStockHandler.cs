using System.Text.Json;
using Inventory.Worker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrderFlow.Contracts;
using OrderFlow.Messaging;

namespace Inventory.Worker.Application;

public enum ReserveStockOutcome
{
    Reserved,
    Rejected,
    AlreadyProcessed
}

public sealed class ReserveStockHandler(InventoryDbContext db, ILogger<ReserveStockHandler> logger)
{
    public async Task<ReserveStockOutcome> HandleAsync(OrderCreated evt, CancellationToken ct = default)
    {
        if (await db.ProcessedEvents.AsNoTracking().AnyAsync(p => p.EventId == evt.EventId, ct))
        {
            logger.LogInformation(
                "Event {EventId} for order {OrderId} already processed; skipping",
                evt.EventId,
                evt.OrderId);
            return ReserveStockOutcome.AlreadyProcessed;
        }

        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var processed = new ProcessedEvent
        {
            EventId = evt.EventId,
            ProcessedAt = now
        };
        db.ProcessedEvents.Add(processed);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            db.Entry(processed).State = EntityState.Detached;
            await tx.RollbackAsync(ct);
            logger.LogInformation(
                "Event {EventId} for order {OrderId} already processed concurrently; skipping",
                evt.EventId,
                evt.OrderId);
            return ReserveStockOutcome.AlreadyProcessed;
        }

        var rowsAffected = evt.Cantidad > 0
            ? await db.Stock
                .Where(s => s.Sku == evt.Sku && s.Disponible >= evt.Cantidad)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Disponible, i => i.Disponible - evt.Cantidad), ct)
            : 0;

        ReserveStockOutcome outcome;

        if (rowsAffected == 1)
        {
            var outboxEventId = Guid.NewGuid();
            var reserved = new StockReserved(
                EventId: outboxEventId,
                OrderId: evt.OrderId,
                Sku: evt.Sku,
                Cantidad: evt.Cantidad,
                OcurridoEn: new DateTimeOffset(now, TimeSpan.Zero));

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = outboxEventId,
                Type = nameof(StockReserved),
                RoutingKey = MessagingConstants.StockReservedRoutingKey,
                Payload = JsonSerializer.Serialize(reserved, EventSerialization.Options),
                CreatedAt = now
            });

            outcome = ReserveStockOutcome.Reserved;
            logger.LogInformation(
                "Reserved {Cantidad} units of {Sku} for order {OrderId} (event {EventId})",
                evt.Cantidad,
                evt.Sku,
                evt.OrderId,
                evt.EventId);
        }
        else
        {
            var skuExists = await db.Stock.AsNoTracking().AnyAsync(s => s.Sku == evt.Sku, ct);
            var reason = skuExists ? RejectionReasons.InsufficientStock : RejectionReasons.UnknownSku;

            var outboxEventId = Guid.NewGuid();
            var rejected = new StockRejected(
                EventId: outboxEventId,
                OrderId: evt.OrderId,
                Sku: evt.Sku,
                Cantidad: evt.Cantidad,
                Motivo: reason,
                OcurridoEn: new DateTimeOffset(now, TimeSpan.Zero));

            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = outboxEventId,
                Type = nameof(StockRejected),
                RoutingKey = MessagingConstants.StockRejectedRoutingKey,
                Payload = JsonSerializer.Serialize(rejected, EventSerialization.Options),
                CreatedAt = now
            });

            outcome = ReserveStockOutcome.Rejected;
            logger.LogInformation(
                "Rejected stock reservation for order {OrderId} ({Sku} x {Cantidad}): {Reason} (event {EventId})",
                evt.OrderId,
                evt.Sku,
                evt.Cantidad,
                reason,
                evt.EventId);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return outcome;
    }

    private static bool IsDuplicateKeyException(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
