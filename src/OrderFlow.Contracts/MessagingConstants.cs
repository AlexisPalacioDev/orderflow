namespace OrderFlow.Contracts;

public static class MessagingConstants
{
    public const string EventsExchange = "orderflow.events";
    public const string RetryExchange = "orderflow.retry";
    public const string DeadLetterExchange = "orderflow.dlx";

    public const string OrderCreatedRoutingKey = "order.created";
    public const string StockReservedRoutingKey = "stock.reserved";
    public const string StockRejectedRoutingKey = "stock.rejected";

    public const string InventoryOrderCreatedQueue = "inventory.order-created";
    public const string InventoryOrderCreatedRetryQueue = "inventory.order-created.retry";
    public const string InventoryOrderCreatedDlq = "inventory.order-created.dlq";

    public const string OrdersStockResultQueue = "orders.stock-result";
    public const string OrdersStockResultRetryQueue = "orders.stock-result.retry";
    public const string OrdersStockResultDlq = "orders.stock-result.dlq";

    public const string AttemptHeader = "x-attempt";
    public const int DefaultMaxDeliveryAttempts = 5;
    public const int DefaultRetryDelayMs = 5000;
    public const ushort ConsumerPrefetch = 10;
}
