using System.Text;
using System.Text.Json;
using Inventory.Worker.Domain;
using OrderFlow.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Inventory.Worker.Messaging;

public sealed class OrderCreatedConsumer(
    IServiceScopeFactory scopeFactory,
    BrokerConnection broker,
    ILogger<OrderCreatedConsumer> logger) : BackgroundService
{
    private const string RetryHeader = "x-retry-count";

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
        var payload = Encoding.UTF8.GetString(ea.Body.ToArray());

        try
        {
            var evt = JsonSerializer.Deserialize<OrderCreated>(payload, EventSerialization.Options)
                ?? throw new InvalidOperationException("Invalid OrderCreated payload.");

            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ReserveStockHandler>();

            await handler.HandleAsync(evt, ct);
            channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            var attempts = GetAttemptCount(ea.BasicProperties);
            if (attempts >= MessagingConstants.MaxDeliveryAttempts)
            {
                logger.LogError(
                    ex,
                    "OrderCreated message failed after {Attempts} attempts; sending to DLQ",
                    attempts);
                channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
            else
            {
                logger.LogWarning(
                    ex,
                    "OrderCreated message failed on attempt {Attempts}; requeuing for retry",
                    attempts);

                var props = channel.CreateBasicProperties();
                props.Persistent = true;
                props.ContentType = "application/json";
                props.Headers = new Dictionary<string, object>
                {
                    [RetryHeader] = attempts
                };

                channel.BasicPublish(
                    exchange: string.Empty,
                    routingKey: MessagingConstants.InventoryOrderCreatedQueue,
                    mandatory: false,
                    basicProperties: props,
                    body: ea.Body);
                channel.BasicAck(ea.DeliveryTag, multiple: false);
            }
        }
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
}
