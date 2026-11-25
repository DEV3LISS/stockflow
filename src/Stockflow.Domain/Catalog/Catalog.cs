using Stockflow.Domain.Common;

namespace Stockflow.Domain.Catalog;

public sealed class Category : Entity, IHasTimestamps
{
    private Category() { }

    public string Name { get; private set; } = null!;

    /// <summary>URL-friendly unique key ("winter-tyres").</summary>
    public string Slug { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Category Create(string name, string slug)
    {
        var category = new Category();
        category.Rename(name, slug);
        return category;
    }

    public void Rename(string name, string slug)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Category name is required.");
        }

        slug = slug.Trim().ToLowerInvariant();
        if (slug.Length is < 2 or > 60 || !slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new DomainException("Slug must be 2–60 characters: lowercase latin letters, digits and dashes.");
        }

        Name = name.Trim();
        Slug = slug;
    }
}

/// <summary>A sellable item. Its stock lives in <see cref="Inventory.StockItem"/> (different change rate and locking needs).</summary>
public sealed class Product : Entity, IHasTimestamps
{
    public const int MaxNameLength = 200;

    private Product() { }

    /// <summary>Stock keeping unit — the shop's own article number. Unique, uppercase.</summary>
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public Guid CategoryId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Product Create(string sku, string name, string? description, decimal price, Guid categoryId)
    {
        var product = new Product { IsActive = true };
        product.Update(sku, name, description, price, categoryId);
        return product;
    }

    public void Update(string sku, string name, string? description, decimal price, Guid categoryId)
    {
        sku = sku.Trim().ToUpperInvariant();
        if (sku.Length is < 3 or > 40 || !sku.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException("SKU must be 3–40 characters: latin letters, digits and dashes.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Product name is required.");
        }

        if (price <= 0 || decimal.Round(price, 2) != price)
        {
            throw new DomainException("Price must be positive with at most 2 decimal places.");
        }

        Sku = sku;
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Price = price;
        CategoryId = categoryId;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
