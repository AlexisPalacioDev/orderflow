using System.Text.Json;

namespace OrderFlow.Contracts;

public record OrderCreated(
    Guid EventId,
    Guid OrderId,
    string Sku,
    int Cantidad,
    DateTimeOffset OcurridoEn);

public record StockReserved(
    Guid EventId,
    Guid OrderId,
    string Sku,
    int Cantidad,
    DateTimeOffset OcurridoEn);

public record StockRejected(
    Guid EventId,
    Guid OrderId,
    string Sku,
    int Cantidad,
    string Motivo,
    DateTimeOffset OcurridoEn);

public static class RejectionReasons
{
    public const string InsufficientStock = "insufficient_stock";
    public const string UnknownSku = "unknown_sku";
}

public static class EventSerialization
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
