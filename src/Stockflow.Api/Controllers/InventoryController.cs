using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockflow.Api.Setup;
using Stockflow.Application.Common;
using Stockflow.Application.Inventory;

namespace Stockflow.Api.Controllers;

/// <summary>Warehouse: receipts, adjustments, ledger, low stock. Admin only.</summary>
[ApiController]
[Route("api/v1/inventory")]
[Authorize(Policy = Policies.AdminOnly)]
[Produces("application/json")]
public sealed class InventoryController(InventoryService inventory) : ControllerBase
{
    [HttpGet("{productId:guid}")]
    public Task<StockDto> Get(Guid productId, CancellationToken ct) => inventory.GetAsync(productId, ct);

    /// <summary>Goods arrived (on_hand += quantity).</summary>
    [HttpPost("{productId:guid}/receipts")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<StockDto> Receive(Guid productId, ReceiveStockRequest request, CancellationToken ct) =>
        inventory.ReceiveAsync(productId, request, ct);

    /// <summary>Correction (±) with a reason. Cannot go below what is reserved by unpaid orders (422).</summary>
    [HttpPost("{productId:guid}/adjustments")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<StockDto> Adjust(Guid productId, AdjustStockRequest request, CancellationToken ct) =>
        inventory.AdjustAsync(productId, request, ct);

    /// <summary>Append-only history of every on-hand change for a product.</summary>
    [HttpGet("{productId:guid}/movements")]
    public Task<PagedResult<MovementDto>> Ledger(Guid productId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        inventory.LedgerAsync(productId, new PageRequest(page, Math.Clamp(pageSize, 1, PageRequest.MaxPageSize)), ct);

    [HttpGet("low-stock")]
    public Task<IReadOnlyList<StockDto>> LowStock([FromQuery] int threshold = 5, CancellationToken ct = default) =>
        inventory.LowStockAsync(threshold, ct);
}
