using System.Text;
using System.Text.Json;
using Inventory.Worker.Application;
using OrderFlow.Contracts;
using OrderFlow.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Inventory.Worker.Messaging;

public sealed class OrderCreatedConsumer(
    IServiceScopeFactory scopeFactory,
    BrokerConnection broker,
    ILogger<OrderCreatedConsumer> logger) : BackgroundService
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
                    queue: MessagingConstants.InventoryOrderCreatedQueue,
                    autoAck: false,
                    consumer: consumer);

                logger.LogInformation("Inventory consumer listening on {Queue}", MessagingConstants.InventoryOrderCreatedQueue);
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
                logger.LogWarning(ex, "Inventory consumer disconnected; retrying in {BackoffSeconds}s", backoffSeconds);
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

        OrderCreated evt;
        try
        {
            evt = ParseEvent(routingKey, messageType, payload);
        }
        catch (PoisonMessageException ex)
        {
            logger.LogError(
                ex,
                "Poison OrderCreated message (RoutingKey={RoutingKey}, Type={MessageType}, MessageId={MessageId}); sending directly to DLQ",
                ea.RoutingKey,
                messageType,
                messageId);
            channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ReserveStockHandler>();

            await handler.HandleAsync(evt, ct);
            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var attempt = RetryDecision.ReadAttempt(ea.BasicProperties?.Headers, MessagingConstants.AttemptHeader);
            if (RetryDecision.For(attempt, broker.MaxDeliveryAttempts) == RetryAction.DeadLetter)
            {
                logger.LogError(
                    ex,
                    "OrderCreated message for order {OrderId} (event {EventId}) failed after {Attempt}/{MaxAttempts} attempts; sending to DLQ",
                    evt.OrderId,
                    evt.EventId,
                    attempt,
                    broker.MaxDeliveryAttempts);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            var nextAttempt = attempt + 1;
            logger.LogWarning(
                ex,
                "OrderCreated message for order {OrderId} (event {EventId}) failed on attempt {Attempt}/{MaxAttempts}; scheduling delayed retry ({DelayMs}ms, next x-attempt={NextAttempt})",
                evt.OrderId,
                evt.EventId,
                attempt,
                broker.MaxDeliveryAttempts,
                broker.RetryDelayMs,
                nextAttempt);

            try
            {
                BrokerConnection.PublishToRetry(retryChannel, ea, routingKey, nextAttempt);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception retryEx)
            {
                logger.LogWarning(
                    retryEx,
                    "Failed to publish retry for order {OrderId} (event {EventId}); leaving message unacknowledged for redelivery",
                    evt.OrderId,
                    evt.EventId);
            }
        }
    }

    private static OrderCreated ParseEvent(string routingKey, string? messageType, string payload)
    {
        if (routingKey != MessagingConstants.OrderCreatedRoutingKey)
        {
            throw new PoisonMessageException($"Unexpected routing key '{routingKey}'.");
        }

        if (!string.IsNullOrWhiteSpace(messageType) && messageType != nameof(OrderCreated))
        {
            throw new PoisonMessageException($"Unexpected message type '{messageType}'.");
        }

        OrderCreated? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OrderCreated>(payload, EventSerialization.Options);
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException("Malformed JSON for OrderCreated.", ex);
        }

        if (evt is null || evt.EventId == Guid.Empty || evt.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(evt.Sku))
        {
            throw new PoisonMessageException("Invalid OrderCreated payload.");
        }

        return evt;
    }
}
