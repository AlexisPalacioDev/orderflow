using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderFlow.Messaging;
using Orders.Api.Data;
using Orders.Api.Endpoints;
using Orders.Api.Messaging;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OrdersDb")
    ?? builder.Configuration["ORDERS_DB_CONNECTION"]
    ?? throw new InvalidOperationException("ConnectionStrings:OrdersDb or ORDERS_DB_CONNECTION is required.");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IOutboxDbContext>(sp => sp.GetRequiredService<OrdersDbContext>());
builder.Services.AddSingleton<BrokerConnection>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<StockResultConsumer>();
builder.Services.AddHealthChecks()
    .AddCheck<OrdersDbHealthCheck>("orders-db", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();

        if (exception is BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Solicitud inválida",
                    Detail = "El cuerpo de la solicitud no es un JSON válido."
                }
            });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Error interno del servidor",
                Detail = "Ocurrió un error inesperado al procesar la solicitud."
            }
        });
    });
});

var migrateOnly = args.Contains("--migrate", StringComparer.OrdinalIgnoreCase);
var runMigrations = migrateOnly
    || !string.Equals(app.Configuration["RUN_MIGRATIONS"], "false", StringComparison.OrdinalIgnoreCase);

if (runMigrations)
{
    await InitializeDatabaseAsync(app.Services, app.Configuration, app.Logger);
}

if (migrateOnly)
{
    return;
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapOrderEndpoints();
app.Run();

static async Task InitializeDatabaseAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
{
    var seedPath = configuration["SEED_PATH"]
        ?? Path.Combine(AppContext.BaseDirectory, "seed", "products.json");

    var deadline = DateTime.UtcNow.AddSeconds(60);
    var delaySeconds = 1;

    while (true)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            await db.Database.MigrateAsync();
            await CatalogSeeder.SeedAsync(db, seedPath, logger);
            return;
        }
        catch (Exception ex) when (DateTime.UtcNow < deadline)
        {
            logger.LogWarning(ex, "Database initialization failed; retrying in {DelaySeconds}s", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            delaySeconds = Math.Min(delaySeconds * 2, 10);
        }
    }
}

sealed class OrdersDbHealthCheck(OrdersDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to Orders database.");
    }
}
