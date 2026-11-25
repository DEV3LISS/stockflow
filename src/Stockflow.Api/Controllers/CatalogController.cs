using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockflow.Api.Setup;
using Stockflow.Application.Catalog;
using Stockflow.Application.Common;
using Stockflow.Domain.Users;

namespace Stockflow.Api.Controllers;

/// <summary>Catalog: anyone can browse; admins manage.</summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class CatalogController(CatalogService catalog) : ControllerBase
{
    [HttpGet("categories")]
    [AllowAnonymous]
    public Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => catalog.ListCategoriesAsync(ct);

    [HttpPost("categories")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<CategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CategoryDto>> CreateCategory(SaveCategoryRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await catalog.CreateCategoryAsync(request, ct));

    /// <summary>Browse products. Admins also see inactive products.</summary>
    /// <param name="category">Category slug.</param>
    /// <param name="search">Part of the name, or an exact SKU.</param>
    /// <param name="sort">Name, PriceAsc, PriceDesc.</param>
    /// <param name="inStock">Only products that can be bought now.</param>
    /// <param name="page">Page number.</param>
    /// <param name="pageSize">Page size (max 100).</param>
    /// <param name="ct">Cancellation.</param>
    [HttpGet("products")]
    [AllowAnonymous]
    public Task<PagedResult<ProductDto>> Products(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] ProductSort sort = ProductSort.Name,
        [FromQuery] bool inStock = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        catalog.ListProductsAsync(new ProductQuery(new PageRequest(page, pageSize), category, search, sort, inStock), IsAdmin, ct);

    [HttpGet("products/{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<ProductDto> Product(Guid id, CancellationToken ct) => catalog.GetProductAsync(id, IsAdmin, ct);

    [HttpPost("products")]
    [Authorize(Policy = Policies.AdminOnly)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductDto>> CreateProduct(SaveProductRequest request, CancellationToken ct)
    {
        var product = await catalog.CreateProductAsync(request, ct);
        return CreatedAtAction(nameof(Product), new { id = product.Id }, product);
    }

    /// <summary>Price changes apply to new orders only.</summary>
    [HttpPut("products/{id:guid}")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<ProductDto> UpdateProduct(Guid id, SaveProductRequest request, CancellationToken ct) => catalog.UpdateProductAsync(id, request, ct);

    [HttpPost("products/{id:guid}/deactivate")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<ProductDto> Deactivate(Guid id, CancellationToken ct) => catalog.SetActiveAsync(id, false, ct);

    [HttpPost("products/{id:guid}/activate")]
    [Authorize(Policy = Policies.AdminOnly)]
    public Task<ProductDto> Activate(Guid id, CancellationToken ct) => catalog.SetActiveAsync(id, true, ct);

    private bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));
}
