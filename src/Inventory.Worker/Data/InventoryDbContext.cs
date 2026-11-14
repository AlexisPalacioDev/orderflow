using Inventory.Worker.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Worker.Data;

public class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> Stock => Set<StockItem>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StockItem>(entity =>
        {
            entity.ToTable("stock");
            entity.HasKey(s => s.Sku);
            entity.Property(s => s.Sku).HasColumnName("sku").HasMaxLength(64);
            entity.Property(s => s.Disponible).HasColumnName("disponible").IsRequired();
        });

        modelBuilder.Entity<ProcessedEvent>(entity =>
        {
            entity.ToTable("processed_events");
            entity.HasKey(p => p.EventId);
            entity.Property(p => p.EventId).HasColumnName("event_id");
            entity.Property(p => p.ProcessedAt).HasColumnName("processed_at").IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
            entity.Property(m => m.RoutingKey).HasColumnName("routing_key").HasMaxLength(100).IsRequired();
            entity.Property(m => m.Payload).HasColumnName("payload").IsRequired();
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(m => m.PublishedAt).HasColumnName("published_at");
            entity.HasIndex(m => m.PublishedAt);
        });
    }
}
