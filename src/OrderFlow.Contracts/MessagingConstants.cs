namespace OrderFlow.Contracts;

public static class MessagingConstants
{
    public const string EventsExchange = "orderflow.events";
    public const string DeadLetterExchange = "orderflow.dlx";

    public const string OrderCreatedRoutingKey = "order.created";
    public const string StockReservedRoutingKey = "stock.reserved";
    public const string StockRejectedRoutingKey = "stock.rejected";

    public const string InventoryOrderCreatedQueue = "inventory.order-created";
    public const string InventoryOrderCreatedDlq = "inventory.order-created.dlq";

    public const string OrdersStockResultQueue = "orders.stock-result";
    public const string OrdersStockResultDlq = "orders.stock-result.dlq";

    public const int MaxDeliveryAttempts = 3;
    public const ushort ConsumerPrefetch = 10;
}
