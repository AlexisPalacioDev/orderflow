using Inventory.Worker.Data;
using Inventory.Worker.Domain;
using Inventory.Worker.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("InventoryDb")
    ?? builder.Configuration["INVENTORY_DB_CONNECTION"]
    ?? throw new InvalidOperationException("ConnectionStrings:InventoryDb or INVENTORY_DB_CONNECTION is required.");

builder.Services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<ReserveStockHandler>();
builder.Services.AddSingleton<BrokerConnection>();
builder.Services.AddHostedService<OrderCreatedConsumer>();
builder.Services.AddHostedService<OutboxPublisher>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory.Worker.Startup");
await InitializeDatabaseAsync(host.Services, builder.Configuration, logger);

await host.RunAsync();

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
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.MigrateAsync();
            await StockSeeder.SeedAsync(db, seedPath, logger);
            return;
        }
        catch (Exception ex) when (DateTime.UtcNow < deadline)
        {
            logger.LogWarning(ex, "Inventory database initialization failed; retrying in {DelaySeconds}s", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            delaySeconds = Math.Min(delaySeconds * 2, 10);
        }
    }
}
