using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Orders.Api.Data;
using Orders.Api.Domain;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Orders.Api.Messaging;

public sealed class StockResultConsumer(
    IServiceScopeFactory scopeFactory,
    BrokerConnection broker,
    ILogger<StockResultConsumer> logger) : BackgroundService
{
    private sealed class PoisonMessageException(string message, Exception? inner = null)
        : Exception(message, inner);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffSeconds = 1;

        while (!stoppingToken.IsCancellationRequested)
        {
            IConnection? connection = null;
            IModel? channel = null;
            IModel? retryChannel = null;

            try
            {
                connection = broker.CreateConnection();
                channel = connection.CreateModel();
                retryChannel = connection.CreateModel();
                broker.DeclareTopology(channel);
                retryChannel.ConfirmSelect();
                channel.BasicQos(0, MessagingConstants.ConsumerPrefetch, global: false);

                var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdown += (_, _) => closedTcs.TrySetResult();
                channel.ModelShutdown += (_, _) => closedTcs.TrySetResult();
                retryChannel.ModelShutdown += (_, _) => closedTcs.TrySetResult();

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, ea) =>
                {
                    await HandleDeliveryAsync(channel, retryChannel, ea, stoppingToken);
                };

                channel.BasicConsume(
                    queue: MessagingConstants.OrdersStockResultQueue,
                    autoAck: false,
                    consumer: consumer);

                logger.LogInformation("Orders consumer listening on {Queue}", MessagingConstants.OrdersStockResultQueue);
                backoffSeconds = 1;

                using var reg = stoppingToken.Register(() => closedTcs.TrySetResult());
                await closedTcs.Task;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Orders consumer disconnected; retrying in {BackoffSeconds}s", backoffSeconds);
            }
            finally
            {
                try { retryChannel?.Dispose(); } catch { /* ignore cleanup errors */ }
                try { channel?.Dispose(); } catch { /* ignore cleanup errors */ }
                try { connection?.Dispose(); } catch { /* ignore cleanup errors */ }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                backoffSeconds = Math.Min(backoffSeconds * 2, 30);
            }
        }
    }

    private async Task HandleDeliveryAsync(
        IModel channel,
        IModel retryChannel,
        BasicDeliverEventArgs ea,
        CancellationToken ct)
    {
        var routingKey = ea.RoutingKey;
        var messageType = ea.BasicProperties?.Type;
        var messageId = ea.BasicProperties?.MessageId;
        var payload = Encoding.UTF8.GetString(ea.Body.ToArray());

        ParsedStockResult parsed;
        try
        {
            parsed = ParseEvent(routingKey, messageType, payload);
        }
        catch (PoisonMessageException ex)
        {
            logger.LogError(
                ex,
                "Poison stock result message (RoutingKey={RoutingKey}, Type={MessageType}, MessageId={MessageId}); sending directly to DLQ",
                routingKey,
                messageType,
                messageId);
            channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

            await ApplyParsedResultAsync(db, parsed, logger, ct);
            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var attempt = RetryDecision.ReadAttempt(ea.BasicProperties?.Headers, MessagingConstants.AttemptHeader);
            if (RetryDecision.For(attempt, broker.MaxDeliveryAttempts) == RetryAction.DeadLetter)
            {
                logger.LogError(
                    ex,
                    "Stock result message for order {OrderId} (event {EventId}) failed after {Attempt}/{MaxAttempts} attempts; sending to DLQ",
                    parsed.OrderId,
                    parsed.EventId,
                    attempt,
                    broker.MaxDeliveryAttempts);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var nextAttempt = attempt + 1;
            logger.LogWarning(
                ex,
                "Stock result message for order {OrderId} (event {EventId}) failed on attempt {Attempt}/{MaxAttempts}; scheduling delayed retry ({DelayMs}ms, next x-attempt={NextAttempt})",
                parsed.OrderId,
                parsed.EventId,
                attempt,
                broker.MaxDeliveryAttempts,
                broker.RetryDelayMs,
                nextAttempt);

            try
            {
                PublishToRetry(retryChannel, ea, routingKey, nextAttempt);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception retryEx)
            {
                logger.LogWarning(
                    retryEx,
                    "Failed to publish retry for order {OrderId} (event {EventId}); leaving message unacknowledged for redelivery",
                    parsed.OrderId,
                    parsed.EventId);
            }
        }
    }

    private static void PublishToRetry(
        IModel retryChannel,
        BasicDeliverEventArgs ea,
        string routingKey,
        int nextAttempt)
    {
        var props = retryChannel.CreateBasicProperties();
        props.Persistent = true;
        props.ContentType = string.IsNullOrWhiteSpace(ea.BasicProperties?.ContentType)
            ? "application/json"
            : ea.BasicProperties.ContentType;

        if (!string.IsNullOrWhiteSpace(ea.BasicProperties?.MessageId))
        {
            props.MessageId = ea.BasicProperties.MessageId;
        }

        if (!string.IsNullOrWhiteSpace(ea.BasicProperties?.Type))
        {
            props.Type = ea.BasicProperties.Type;
        }

        if (!string.IsNullOrWhiteSpace(ea.BasicProperties?.CorrelationId))
        {
            props.CorrelationId = ea.BasicProperties.CorrelationId;
        }

        var headers = ea.BasicProperties?.Headers is not null
            ? new Dictionary<string, object>(ea.BasicProperties.Headers)
            : new Dictionary<string, object>();
        headers[MessagingConstants.AttemptHeader] = nextAttempt;
        props.Headers = headers;

        retryChannel.BasicPublish(
            exchange: MessagingConstants.RetryExchange,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: ea.Body);

        retryChannel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
    }

    public static async Task<bool> ApplyResultAsync(
        OrdersDbContext db,
        string routingKey,
        string payload,
        ILogger logger,
        CancellationToken ct = default)
    {
        var parsed = ParseEvent(routingKey, messageType: null, payload);
        return await ApplyParsedResultAsync(db, parsed, logger, ct);
    }

    private static async Task<bool> ApplyParsedResultAsync(
        OrdersDbContext db,
        ParsedStockResult parsed,
        ILogger logger,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (parsed.Reserved is not null)
        {
            var evt = parsed.Reserved;
            var updated = await db.Orders
                .Where(o => o.Id == evt.OrderId && o.Estado == OrderStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Estado, OrderStatus.Confirmed)
                    .SetProperty(o => o.MotivoRechazo, (string?)null)
                    .SetProperty(o => o.ActualizadoEn, now), ct);

            if (updated == 1)
            {
                logger.LogInformation(
                    "Order {OrderId} transitioned Pending -> Confirmed via event {EventId}",
                    evt.OrderId,
                    evt.EventId);
            }
            else
            {
                logger.LogInformation(
                    "Ignored StockReserved event {EventId} for order {OrderId}; order is not Pending",
                    evt.EventId,
                    evt.OrderId);
            }

            return updated == 1;
        }

        var rejected = parsed.Rejected!;
        var rejectedUpdated = await db.Orders
            .Where(o => o.Id == rejected.OrderId && o.Estado == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Estado, OrderStatus.Rejected)
                .SetProperty(o => o.MotivoRechazo, rejected.Motivo)
                .SetProperty(o => o.ActualizadoEn, now), ct);

        if (rejectedUpdated == 1)
        {
            logger.LogInformation(
                "Order {OrderId} transitioned Pending -> Rejected ({Reason}) via event {EventId}",
                rejected.OrderId,
                rejected.Motivo,
                rejected.EventId);
        }
        else
        {
            logger.LogInformation(
                "Ignored StockRejected event {EventId} for order {OrderId}; order is not Pending",
                rejected.EventId,
                rejected.OrderId);
        }

        return rejectedUpdated == 1;
    }

    private static ParsedStockResult ParseEvent(string routingKey, string? messageType, string payload)
    {
        if (routingKey == MessagingConstants.StockReservedRoutingKey)
        {
            if (!string.IsNullOrWhiteSpace(messageType) && messageType != nameof(StockReserved))
            {
                throw new PoisonMessageException($"Unexpected message type '{messageType}' for routing key '{routingKey}'.");
            }

            StockReserved? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StockReserved>(payload, EventSerialization.Options);
            }
            catch (JsonException ex)
            {
                throw new PoisonMessageException("Malformed JSON for StockReserved.", ex);
            }

            if (evt is null || evt.EventId == Guid.Empty || evt.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(evt.Sku))
            {
                throw new PoisonMessageException("Invalid StockReserved payload.");
            }

            return new ParsedStockResult(evt.OrderId, evt.EventId, evt, null);
        }

        if (routingKey == MessagingConstants.StockRejectedRoutingKey)
        {
            if (!string.IsNullOrWhiteSpace(messageType) && messageType != nameof(StockRejected))
            {
                throw new PoisonMessageException($"Unexpected message type '{messageType}' for routing key '{routingKey}'.");
            }

            StockRejected? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StockRejected>(payload, EventSerialization.Options);
            }
            catch (JsonException ex)
            {
                throw new PoisonMessageException("Malformed JSON for StockRejected.", ex);
            }

            if (evt is null ||
                evt.EventId == Guid.Empty ||
                evt.OrderId == Guid.Empty ||
                string.IsNullOrWhiteSpace(evt.Sku) ||
                string.IsNullOrWhiteSpace(evt.Motivo))
            {
                throw new PoisonMessageException("Invalid StockRejected payload.");
            }

            return new ParsedStockResult(evt.OrderId, evt.EventId, null, evt);
        }

        throw new PoisonMessageException($"Unsupported routing key '{routingKey}'.");
    }

    private sealed record ParsedStockResult(
        Guid OrderId,
        Guid EventId,
        StockReserved? Reserved,
        StockRejected? Rejected);
}
