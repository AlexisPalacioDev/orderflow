using OrderFlow.Contracts;
using RabbitMQ.Client;

namespace Orders.Api.Messaging;

public sealed class BrokerConnection
{
    private readonly ConnectionFactory _factory;

    public BrokerConnection(IConfiguration configuration)
    {
        var host = configuration["RABBITMQ_HOST"] ?? configuration["RabbitMQ:Host"]
            ?? throw new InvalidOperationException("RABBITMQ_HOST environment variable is required.");
        var portRaw = configuration["RABBITMQ_PORT"] ?? configuration["RabbitMQ:Port"] ?? "5672";
        var user = configuration["RABBITMQ_USER"] ?? configuration["RabbitMQ:User"]
            ?? throw new InvalidOperationException("RABBITMQ_USER environment variable is required.");
        var password = configuration["RABBITMQ_PASSWORD"] ?? configuration["RabbitMQ:Password"]
            ?? throw new InvalidOperationException("RABBITMQ_PASSWORD environment variable is required.");

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

    public static void DeclareTopology(IModel channel)
    {
        channel.ExchangeDeclare(
            exchange: MessagingConstants.EventsExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);

        channel.ExchangeDeclare(
            exchange: MessagingConstants.DeadLetterExchange,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false);

        DeclareQueueWithDlq(
            channel,
            MessagingConstants.InventoryOrderCreatedQueue,
            MessagingConstants.InventoryOrderCreatedDlq,
            [MessagingConstants.OrderCreatedRoutingKey]);

        DeclareQueueWithDlq(
            channel,
            MessagingConstants.OrdersStockResultQueue,
            MessagingConstants.OrdersStockResultDlq,
            [MessagingConstants.StockReservedRoutingKey, MessagingConstants.StockRejectedRoutingKey]);
    }

    private static void DeclareQueueWithDlq(
        IModel channel,
        string queueName,
        string dlqName,
        string[] routingKeys)
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

        var args = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = MessagingConstants.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = dlqName
        };

        channel.QueueDeclare(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: args);

        foreach (var key in routingKeys)
        {
            channel.QueueBind(
                queue: queueName,
                exchange: MessagingConstants.EventsExchange,
                routingKey: key);
        }
    }
}
