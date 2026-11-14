namespace Orders.Api.Domain;

public static class OrderValidator
{
    public static Dictionary<string, string[]> Validate(string? clienteNombre, string? sku, int cantidad, bool skuExists)
    {
        var errors = new Dictionary<string, string[]>();

        var nombre = clienteNombre?.Trim();
        if (string.IsNullOrEmpty(nombre))
        {
            errors["clienteNombre"] = ["El nombre del cliente es obligatorio."];
        }
        else if (nombre.Length > 120)
        {
            errors["clienteNombre"] = ["El nombre del cliente no puede superar los 120 caracteres."];
        }

        var trimmedSku = sku?.Trim();
        if (string.IsNullOrEmpty(trimmedSku))
        {
            errors["sku"] = ["El SKU es obligatorio."];
        }
        else if (!skuExists)
        {
            errors["sku"] = [$"El SKU '{trimmedSku}' no existe en el catálogo."];
        }

        if (cantidad < 1 || cantidad > 100)
        {
            errors["cantidad"] = ["La cantidad debe estar entre 1 y 100."];
        }

        return errors;
    }
}
