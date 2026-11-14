using Microsoft.EntityFrameworkCore;
using Orders.Api.Domain;

namespace Orders.Api.Data;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasColumnName("id");
            entity.Property(o => o.ClienteNombre).HasColumnName("cliente_nombre").HasMaxLength(120).IsRequired();
            entity.Property(o => o.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
            entity.Property(o => o.Cantidad).HasColumnName("cantidad").IsRequired();
            entity.Property(o => o.Estado)
                .HasColumnName("estado")
                .HasMaxLength(20)
                .HasConversion<string>()
                .IsRequired();
            entity.Property(o => o.CreadoEn).HasColumnName("creado_en").IsRequired();
            entity.Property(o => o.ActualizadoEn).HasColumnName("actualizado_en").IsRequired();
            entity.Property(o => o.MotivoRechazo).HasColumnName("motivo_rechazo").HasMaxLength(200);
            entity.HasIndex(o => o.CreadoEn);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(p => p.Sku);
            entity.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(64);
            entity.Property(p => p.Nombre).HasColumnName("nombre").HasMaxLength(200).IsRequired();
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
