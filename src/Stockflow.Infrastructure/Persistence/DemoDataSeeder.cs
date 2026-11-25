using Microsoft.EntityFrameworkCore;
using Stockflow.Domain.Catalog;
using Stockflow.Domain.Inventory;

namespace Stockflow.Infrastructure.Persistence;

/// <summary>Sample catalog for demos (Seed:DemoData = true). Stock is added through receipts, so the ledger is consistent.</summary>
internal static class DemoDataSeeder
{
    public static async Task<bool> SeedAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(ct))
        {
            return false;
        }

        var phones = Category.Create("Phones", "phones");
        var laptops = Category.Create("Laptops", "laptops");
        var audio = Category.Create("Audio", "audio");
        db.Categories.AddRange(phones, laptops, audio);

        (string Sku, string Name, decimal Price, Category Category, int Stock)[] items =
        [
            ("PH-PIXEL-9", "Pixel 9 128 GB", 69990m, phones, 25),
            ("PH-GALAXY-S25", "Galaxy S25 256 GB", 89990m, phones, 15),
            ("PH-REDMI-14", "Redmi Note 14", 24990m, phones, 60),
            ("PH-LIMITED-1", "Limited Edition Phone (flash sale)", 9990m, phones, 1),
            ("LT-AIR-13", "Laptop Air 13", 109990m, laptops, 8),
            ("LT-PRO-16", "Laptop Pro 16", 249990m, laptops, 3),
            ("LT-GAMER-15", "Gaming Laptop 15", 154990m, laptops, 5),
            ("AU-BUDS-3", "Wireless Buds 3", 12990m, audio, 120),
            ("AU-HEAD-X", "Noise Cancelling Headphones X", 34990m, audio, 30),
            ("AU-SPK-MINI", "Portable Speaker Mini", 5990m, audio, 0),
        ];

        foreach (var item in items)
        {
            var product = Product.Create(item.Sku, item.Name, null, item.Price, item.Category.Id);
            db.Products.Add(product);
            var stock = StockItem.For(product.Id, now);
            db.StockItems.Add(stock);
            if (item.Stock > 0)
            {
                db.InventoryMovements.Add(stock.Receive(item.Stock, userId: null, "DEMO-INITIAL", now));
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}
