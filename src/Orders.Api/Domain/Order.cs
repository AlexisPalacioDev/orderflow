namespace Orders.Api.Domain;

public class Order
{
    public Guid Id { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int Cantidad { get; set; }
    public OrderStatus Estado { get; set; } = OrderStatus.Pending;
    public DateTime CreadoEn { get; set; }
    public DateTime ActualizadoEn { get; set; }
    public string? MotivoRechazo { get; set; }
}
