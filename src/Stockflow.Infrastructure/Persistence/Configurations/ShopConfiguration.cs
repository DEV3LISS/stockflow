using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockflow.Domain.Carts;
using Stockflow.Domain.Catalog;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;
using Stockflow.Domain.Users;

namespace Stockflow.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Slug).HasMaxLength(60).IsRequired();
        builder.HasIndex(c => c.Slug).IsUnique();
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t => t.HasCheckConstraint("ck_products_price", "price > 0"));
        builder.Property(p => p.Sku).HasMaxLength(40).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(4000);
        builder.Property(p => p.Price).HasPrecision(12, 2);
        builder.HasIndex(p => p.Sku).IsUnique();
        builder.HasIndex(p => new { p.CategoryId, p.IsActive });
        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        // + trigram index on lower(name) for "contains" search, in the migration (EF cannot express it).
    }
}

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("stock_items", t =>
        {
            // The core inventory invariants, enforced by PostgreSQL for every code path (and every SQL script).
            t.HasCheckConstraint("ck_stock_items_on_hand", "on_hand >= 0");
            t.HasCheckConstraint("ck_stock_items_reserved", "reserved >= 0 AND reserved <= on_hand");
        });
        builder.HasKey(s => s.ProductId);
        builder.Ignore(s => s.Available);
        builder.HasOne<Product>().WithOne().HasForeignKey<StockItem>(s => s.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property<uint>("Version").IsRowVersion(); // admin receipts/adjustments: optimistic concurrency
    }
}

internal sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.ToTable("inventory_movements", t =>
            t.HasCheckConstraint("ck_inventory_movements_quantity", "quantity <> 0"));
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();
        builder.Property(m => m.Reference).HasMaxLength(100);
        builder.Property(m => m.Reason).HasMaxLength(200);
        builder.HasIndex(m => new { m.ProductId, m.Id });
        builder.HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.ToTable("carts");
        builder.HasIndex(c => c.CustomerId).IsUnique(); // one cart per customer
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Items).WithOne().HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.ToTable("cart_items", t => t.HasCheckConstraint("ck_cart_items_quantity", "quantity BETWEEN 1 AND 20"));
        builder.HasKey(i => new { i.CartId, i.ProductId });
        builder.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_status", "status IN ('PendingPayment', 'Paid', 'Shipped', 'Cancelled', 'Expired')");
            t.HasCheckConstraint("ck_orders_total", "total > 0");
        });
        builder.Property(o => o.Number).UseIdentityAlwaysColumn().HasIdentityOptions(startValue: 10001);
        builder.HasIndex(o => o.Number).IsUnique();
        builder.Property(o => o.Total).HasPrecision(12, 2);
        builder.Property(o => o.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Ignore(o => o.HoldsReservation);

        // Retried checkouts: one order per (customer, key).
        builder.HasIndex(o => new { o.CustomerId, o.IdempotencyKey }).IsUnique();
        builder.HasIndex(o => new { o.CustomerId, o.Number });
        // The expiry job's query — a tiny partial index: only orders that still hold stock.
        builder.HasIndex(o => o.ReservedUntil).HasFilter("status = 'PendingPayment'");

        builder.HasOne<User>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", t => t.HasCheckConstraint("ck_order_lines_quantity", "quantity > 0"));
        builder.HasKey(l => new { l.OrderId, l.ProductId });
        builder.Property(l => l.Sku).HasMaxLength(40).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
        builder.Property(l => l.UnitPrice).HasPrecision(12, 2);
        builder.Property(l => l.LineTotal).HasPrecision(12, 2);
        builder.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => l.ProductId);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", t =>
            t.HasCheckConstraint("ck_payments_status", "status IN ('Pending', 'Succeeded', 'Failed', 'NeedsRefund')"));
        builder.Property(p => p.ProviderPaymentId).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Amount).HasPrecision(12, 2);
        builder.Ignore(p => p.IsFinal);
        builder.HasIndex(p => p.ProviderPaymentId).IsUnique();
        builder.HasIndex(p => p.OrderId);
        builder.HasIndex(p => p.Status).HasFilter("status = 'NeedsRefund'");
        builder.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> builder)
    {
        builder.ToTable("processed_webhook_events");
        builder.HasKey(e => e.EventId); // the dedup key itself
        builder.Property(e => e.EventId).HasMaxLength(100);
        builder.Property(e => e.Type).HasMaxLength(50).IsRequired();
    }
}
