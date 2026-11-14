using Microsoft.Extensions.Configuration;
using OrderFlow.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderFlow.Messaging;

public sealed class BrokerConnection
{
    private readonly ConnectionFactory _factory;

    public int RetryDelayMs { get; }
    public int MaxDeliveryAttempts { get; }

    public BrokerConnection(IConfiguration configuration)
    {
        var host = configuration["RABBITMQ_HOST"] ?? configuration["RabbitMQ:Host"]
            ?? throw new InvalidOperationException("RABBITMQ_HOST environment variable is required.");
        var portRaw = configuration["RABBITMQ_PORT"] ?? configuration["RabbitMQ:Port"] ?? "5672";
        var user = configuration["RABBITMQ_USER"] ?? configuration["RabbitMQ:User"]
            ?? throw new InvalidOperationException("RABBITMQ_USER environment variable is required.");
        var password = configuration["RABBITMQ_PASSWORD"] ?? configuration["RabbitMQ:Password"]
            ?? throw new InvalidOperationException("RABBITMQ_PASSWORD environment variable is required.");

        RetryDelayMs = int.TryParse(configuration["RETRY_DELAY_MS"], out var delayMs) && delayMs > 0
            ? delayMs
            : MessagingConstants.DefaultRetryDelayMs;

        MaxDeliveryAttempts = int.TryParse(configuration["MAX_DELIVERY_ATTEMPTS"], out var maxAttempts) && maxAttempts > 0
            ? maxAttempts
            : MessagingConstants.DefaultMaxDeliveryAttempts;

        _factory = new ConnectionFactory
        {
            HostName = host,
            Port = int.Parse(portRaw),
            UserName = user,
            Password = password,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = false
        };
    }

    public IConnection CreateConnection() => _factory.CreateConnection();

    public void DeclareTopology(IModel channel) => DeclareTopology(channel, RetryDelayMs);

    public static void DeclareTopology(IModel channel, int retryDelayMs)
    {
        channel.ExchangeDeclare(
            exchange: MessagingConstants.EventsExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);

        channel.ExchangeDeclare(
            exchange: MessagingConstants.RetryExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        channel.ExchangeDeclare(
            exchange: MessagingConstants.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        DeclareQueueTopology(
            channel,
            MessagingConstants.InventoryOrderCreatedQueue,
            MessagingConstants.InventoryOrderCreatedRetryQueue,
            MessagingConstants.InventoryOrderCreatedDlq,
            [MessagingConstants.OrderCreatedRoutingKey],
            retryDelayMs);

        DeclareQueueTopology(
            channel,
            MessagingConstants.OrdersStockResultQueue,
            MessagingConstants.OrdersStockResultRetryQueue,
            MessagingConstants.OrdersStockResultDlq,
            [MessagingConstants.StockReservedRoutingKey, MessagingConstants.StockRejectedRoutingKey],
            retryDelayMs);
    }

    private static void DeclareQueueTopology(
        IModel channel,
        string queueName,
        string retryQueueName,
        string dlqName,
        string[] routingKeys,
        int retryDelayMs)
    {
        channel.QueueDeclare(
            queue: dlqName,
            durable: true,
            exclusive: false,
            autoDelete: false);

        channel.QueueBind(
            queue: dlqName,
            exchange: MessagingConstants.DeadLetterExchange,
            routingKey: dlqName);

        var workArgs = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = MessagingConstants.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = dlqName
        };

        channel.QueueDeclare(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: workArgs);

        // Omit x-dead-letter-routing-key so expired messages return to orderflow.events with their original routing key.
        var retryArgs = new Dictionary<string, object>
        {
            ["x-message-ttl"] = retryDelayMs,
            ["x-dead-letter-exchange"] = MessagingConstants.EventsExchange
        };

        channel.QueueDeclare(
            queue: retryQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: retryArgs);

        foreach (var key in routingKeys)
        {
            channel.QueueBind(
                queue: queueName,
                exchange: MessagingConstants.EventsExchange,
                routingKey: key);

            channel.QueueBind(
                queue: retryQueueName,
                exchange: MessagingConstants.RetryExchange,
                routingKey: key);
        }
    }

    public static void PublishToRetry(
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
}
