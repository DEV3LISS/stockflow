using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Domain.Catalog;
using Stockflow.Domain.Inventory;

namespace Stockflow.Application.Catalog;

public sealed record CategoryDto(Guid Id, string Name, string Slug);

public sealed record SaveCategoryRequest(string Name, string Slug);

public sealed record ProductDto(Guid Id, string Sku, string Name, string? Description, decimal Price, Guid CategoryId,
    string CategorySlug, bool IsActive, int Available);

public sealed record SaveProductRequest(string Sku, string Name, string? Description, decimal Price, Guid CategoryId);

public enum ProductSort
{
    Name,
    PriceAsc,
    PriceDesc,
}

public sealed record ProductQuery(PageRequest Paging, string? Category = null, string? Search = null, ProductSort Sort = ProductSort.Name,
    bool InStockOnly = false);

public sealed class SaveCategoryRequestValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(60);
    }
}

public sealed class SaveProductRequestValidator : AbstractValidator<SaveProductRequest>
{
    public SaveProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(x => x.Description).MaximumLength(4000);
        RuleFor(x => x.Price).GreaterThan(0).PrecisionScale(12, 2, true);
        RuleFor(x => x.CategoryId).NotEmpty();
    }
}

public sealed class ProductQueryValidator : AbstractValidator<ProductQuery>
{
    public ProductQueryValidator()
    {
        RuleFor(x => x.Paging).ValidPage();
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Category).MaximumLength(60);
        RuleFor(x => x.Sort).IsInEnum();
    }
}

/// <summary>Catalog: public browsing and admin management of categories and products.</summary>
public sealed class CatalogService(
    IAppDbContext db,
    TimeProvider clock,
    IValidator<SaveCategoryRequest> categoryValidator,
    IValidator<SaveProductRequest> productValidator,
    IValidator<ProductQuery> queryValidator)
{
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        await db.Categories.AsNoTracking().OrderBy(c => c.Name).Select(c => new CategoryDto(c.Id, c.Name, c.Slug)).ToListAsync(ct);

    public async Task<CategoryDto> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct)
    {
        await categoryValidator.ValidateAndThrowAsync(request, ct);
        var category = Category.Create(request.Name, request.Slug);
        if (await db.Categories.AnyAsync(c => c.Slug == category.Slug, ct))
        {
            throw new ConflictException($"Category '{category.Slug}' already exists.");
        }

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return new CategoryDto(category.Id, category.Name, category.Slug);
    }

    /// <summary>
    /// Public product listing. Availability comes from a join with stock_items in the same query (no N+1).
    /// </summary>
    public async Task<PagedResult<ProductDto>> ListProductsAsync(ProductQuery query, bool includeInactive, CancellationToken ct)
    {
        await queryValidator.ValidateAndThrowAsync(query, ct);
        var rows = Project(includeInactive);

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var slug = query.Category.Trim().ToLowerInvariant();
            rows = rows.Where(p => p.CategorySlug == slug);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = Search.ContainsPattern(query.Search.Trim().ToLowerInvariant());
            var sku = query.Search.Trim().ToUpperInvariant(); // exact SKU match, e.g. a scanned barcode
#pragma warning disable CA1304, CA1311 // translated to SQL lower()
            rows = rows.Where(p => EF.Functions.Like(p.Name.ToLower(), pattern, @"\") || p.Sku == sku);
#pragma warning restore CA1304, CA1311
        }

        if (query.InStockOnly)
        {
            rows = rows.Where(p => p.Available > 0);
        }

        rows = query.Sort switch
        {
            ProductSort.PriceAsc => rows.OrderBy(p => p.Price).ThenBy(p => p.Id),
            ProductSort.PriceDesc => rows.OrderByDescending(p => p.Price).ThenBy(p => p.Id),
            _ => rows.OrderBy(p => p.Name).ThenBy(p => p.Id),
        };

        var total = await rows.CountAsync(ct);
        var items = await rows.Skip(query.Paging.Skip).Take(query.Paging.PageSize).Select(ToDto).ToListAsync(ct);
        return new PagedResult<ProductDto>(items, query.Paging.Page, query.Paging.PageSize, total);
    }

    public async Task<ProductDto> GetProductAsync(Guid id, bool includeInactive, CancellationToken ct) =>
        await Project(includeInactive).Where(p => p.Id == id).Select(ToDto).SingleOrDefaultAsync(ct)
        ?? throw new NotFoundException("Product", id);

    public async Task<ProductDto> CreateProductAsync(SaveProductRequest request, CancellationToken ct)
    {
        await productValidator.ValidateAndThrowAsync(request, ct);
        await EnsureCategoryExistsAsync(request.CategoryId, ct);
        var product = Product.Create(request.Sku, request.Name, request.Description, request.Price, request.CategoryId);
        if (await db.Products.AnyAsync(p => p.Sku == product.Sku, ct))
        {
            throw new ConflictException($"SKU '{product.Sku}' already exists.");
        }

        db.Products.Add(product);
        db.StockItems.Add(StockItem.For(product.Id, clock.GetUtcNow())); // every product has a stock row from day one
        await db.SaveChangesAsync(ct);
        return await GetProductAsync(product.Id, includeInactive: true, ct);
    }

    /// <summary>Price changes affect only future orders: existing orders keep their price snapshot.</summary>
    public async Task<ProductDto> UpdateProductAsync(Guid id, SaveProductRequest request, CancellationToken ct)
    {
        await productValidator.ValidateAndThrowAsync(request, ct);
        await EnsureCategoryExistsAsync(request.CategoryId, ct);
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product", id);
        product.Update(request.Sku, request.Name, request.Description, request.Price, request.CategoryId);
        if (await db.Products.AnyAsync(p => p.Sku == product.Sku && p.Id != id, ct))
        {
            throw new ConflictException($"SKU '{product.Sku}' already exists.");
        }

        await db.SaveChangesAsync(ct);
        return await GetProductAsync(id, includeInactive: true, ct);
    }

    public async Task<ProductDto> SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Product", id);
        if (active)
        {
            product.Activate();
        }
        else
        {
            product.Deactivate();
        }

        await db.SaveChangesAsync(ct);
        return await GetProductAsync(id, includeInactive: true, ct);
    }

    /// <summary>
    /// Joined row with settable properties: EF Core can translate filters and sorting on it into SQL.
    /// (Filtering on a positional record built with a constructor cannot be translated — it would fail at runtime.)
    /// </summary>
    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public string Sku { get; init; } = null!;
        public string Name { get; init; } = null!;
        public string? Description { get; init; }
        public decimal Price { get; init; }
        public Guid CategoryId { get; init; }
        public string CategorySlug { get; init; } = null!;
        public bool IsActive { get; init; }
        public int Available { get; init; }
    }

    private static readonly System.Linq.Expressions.Expression<Func<ProductRow, ProductDto>> ToDto = p =>
        new ProductDto(p.Id, p.Sku, p.Name, p.Description, p.Price, p.CategoryId, p.CategorySlug, p.IsActive, p.Available);

    private IQueryable<ProductRow> Project(bool includeInactive) =>
        from p in db.Products.AsNoTracking()
        join c in db.Categories on p.CategoryId equals c.Id
        join s in db.StockItems on p.Id equals s.ProductId
        where includeInactive || p.IsActive
        select new ProductRow
        {
            Id = p.Id, Sku = p.Sku, Name = p.Name, Description = p.Description, Price = p.Price,
            CategoryId = c.Id, CategorySlug = c.Slug, IsActive = p.IsActive, Available = s.OnHand - s.Reserved,
        };

    private async Task EnsureCategoryExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == id, ct))
        {
            throw new NotFoundException("Category", id);
        }
    }
}
