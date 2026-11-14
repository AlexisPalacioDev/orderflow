using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Orders.Api.Data;
using RabbitMQ.Client;

namespace Orders.Api.Messaging;

public sealed class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    BrokerConnection broker,
    IConfiguration configuration,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(
        int.TryParse(configuration["OUTBOX_POLL_MS"], out var ms) && ms > 0 ? ms : 1000);

    private IConnection? _connection;
    private IModel? _channel;
    private int _backoffSeconds = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishPendingBatchAsync(stoppingToken);
                _backoffSeconds = 1;

                if (published == 0)
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox publish cycle failed; retrying in {BackoffSeconds}s", _backoffSeconds);
                ResetConnection();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_backoffSeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _backoffSeconds = Math.Min(_backoffSeconds * 2, 30);
            }
        }

        ResetConnection();
    }

    private async Task<int> PublishPendingBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        var pending = await db.OutboxMessages
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return 0;
        }

        EnsureChannel();

        foreach (var message in pending)
        {
            var props = _channel!.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.MessageId = message.Id.ToString();
            props.Type = message.Type;

            var body = Encoding.UTF8.GetBytes(message.Payload);

            _channel.BasicPublish(
                exchange: MessagingConstants.EventsExchange,
                routingKey: message.RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body);

            _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));

            message.PublishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Published outbox event {EventId} ({EventType}) to {RoutingKey}",
                message.Id,
                message.Type,
                message.RoutingKey);
        }

        return pending.Count;
    }

    private void EnsureChannel()
    {
        if (_channel is { IsOpen: true } && _connection is { IsOpen: true })
        {
            return;
        }

        ResetConnection();

        _connection = broker.CreateConnection();
        _channel = _connection.CreateModel();
        broker.DeclareTopology(_channel);
        _channel.ConfirmSelect();
    }

    private void ResetConnection()
    {
        try { _channel?.Dispose(); } catch { /* ignore cleanup errors */ }
        try { _connection?.Dispose(); } catch { /* ignore cleanup errors */ }
        _channel = null;
        _connection = null;
    }
}
