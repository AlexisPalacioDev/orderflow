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
    private const string RetryHeader = "x-retry-count";
    private const string OriginalRoutingKeyHeader = "x-original-routing-key";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffSeconds = 1;

        while (!stoppingToken.IsCancellationRequested)
        {
            IConnection? connection = null;
            IModel? channel = null;

            try
            {
                connection = broker.CreateConnection();
                channel = connection.CreateModel();
                BrokerConnection.DeclareTopology(channel);
                channel.BasicQos(0, MessagingConstants.ConsumerPrefetch, global: false);

                var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdown += (_, _) => closedTcs.TrySetResult();
                channel.ModelShutdown += (_, _) => closedTcs.TrySetResult();

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.Received += async (_, ea) =>
                {
                    await HandleDeliveryAsync(channel, ea, stoppingToken);
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

    private async Task HandleDeliveryAsync(IModel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var routingKey = ResolveRoutingKey(ea);
        var payload = Encoding.UTF8.GetString(ea.Body.ToArray());

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

            await ApplyResultAsync(db, routingKey, payload, logger, ct);
            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var attempts = GetAttemptCount(ea.BasicProperties);
            if (attempts >= MessagingConstants.MaxDeliveryAttempts)
            {
                logger.LogError(
                    ex,
                    "Stock result message failed after {Attempts} attempts; sending to DLQ",
                    attempts);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "Stock result message failed on attempt {Attempts}; requeuing for retry",
                    attempts);

                var props = channel.CreateBasicProperties();
                props.Persistent = true;
                props.ContentType = "application/json";
                props.Headers = new Dictionary<string, object>
                {
                    [RetryHeader] = attempts,
                    [OriginalRoutingKeyHeader] = routingKey
                };

                channel.BasicPublish(
                    exchange: string.Empty,
                    routingKey: MessagingConstants.OrdersStockResultQueue,
                    mandatory: false,
                    basicProperties: props,
                    body: ea.Body);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
        }
    }

    public static async Task<bool> ApplyResultAsync(
        OrdersDbContext db,
        string routingKey,
        string payload,
        ILogger logger,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (routingKey == MessagingConstants.StockReservedRoutingKey)
        {
            var evt = JsonSerializer.Deserialize<StockReserved>(payload, EventSerialization.Options)
                ?? throw new InvalidOperationException("Invalid StockReserved payload.");

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

        if (routingKey == MessagingConstants.StockRejectedRoutingKey)
        {
            var evt = JsonSerializer.Deserialize<StockRejected>(payload, EventSerialization.Options)
                ?? throw new InvalidOperationException("Invalid StockRejected payload.");

            var updated = await db.Orders
                .Where(o => o.Id == evt.OrderId && o.Estado == OrderStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.Estado, OrderStatus.Rejected)
                    .SetProperty(o => o.MotivoRechazo, evt.Motivo)
                    .SetProperty(o => o.ActualizadoEn, now), ct);

            if (updated == 1)
            {
                logger.LogInformation(
                    "Order {OrderId} transitioned Pending -> Rejected ({Reason}) via event {EventId}",
                    evt.OrderId,
                    evt.Motivo,
                    evt.EventId);
            }
            else
            {
                logger.LogInformation(
                    "Ignored StockRejected event {EventId} for order {OrderId}; order is not Pending",
                    evt.EventId,
                    evt.OrderId);
            }

            return updated == 1;
        }

        throw new InvalidOperationException($"Unsupported routing key '{routingKey}'.");
    }

    private static int GetAttemptCount(IBasicProperties? properties)
    {
        if (properties?.Headers is null || !properties.Headers.TryGetValue(RetryHeader, out var raw))
        {
            return 1;
        }

        var previousRetries = raw switch
        {
            int i => i,
            long l => (int)l,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => 0
        };

        return previousRetries + 1;
    }

    private static string ResolveRoutingKey(BasicDeliverEventArgs ea)
    {
        if (ea.BasicProperties?.Headers is not null &&
            ea.BasicProperties.Headers.TryGetValue(OriginalRoutingKeyHeader, out var raw))
        {
            if (raw is byte[] bytes)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            if (raw is string text)
            {
                return text;
            }
        }

        return ea.RoutingKey;
    }
}
