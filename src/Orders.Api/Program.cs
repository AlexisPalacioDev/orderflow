using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<IOutboxDbContext>(sp => sp.GetRequiredService<OrdersDbContext>());
builder.Services.AddSingleton<BrokerConnection>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<StockResultConsumer>();

var app = builder.Build();

await InitializeDatabaseAsync(app.Services, app.Configuration, app.Logger);

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
