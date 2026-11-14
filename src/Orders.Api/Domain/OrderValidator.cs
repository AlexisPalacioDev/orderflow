namespace Orders.Api.Domain;

public record CreateOrderRequest(string? ClienteNombre, string? Sku, int Cantidad);

public static class OrderValidator
{
    public static Dictionary<string, string[]> Validate(CreateOrderRequest request, bool skuExists)
    {
        var errors = new Dictionary<string, string[]>();

        var nombre = request.ClienteNombre?.Trim();
        if (string.IsNullOrEmpty(nombre))
        {
            errors["clienteNombre"] = ["El nombre del cliente es obligatorio."];
        }
        else if (nombre.Length > 120)
        {
            errors["clienteNombre"] = ["El nombre del cliente no puede superar los 120 caracteres."];
        }

        var sku = request.Sku?.Trim();
        if (string.IsNullOrEmpty(sku))
        {
            errors["sku"] = ["El SKU es obligatorio."];
        }
        else if (!skuExists)
        {
            errors["sku"] = [$"El SKU '{sku}' no existe en el catálogo."];
        }

        if (request.Cantidad < 1 || request.Cantidad > 100)
        {
            errors["cantidad"] = ["La cantidad debe estar entre 1 y 100."];
        }

        return errors;
    }
}
