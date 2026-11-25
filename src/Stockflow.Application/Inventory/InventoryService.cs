using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Domain.Inventory;

namespace Stockflow.Application.Inventory;

public sealed record StockDto(Guid ProductId, string Sku, string Name, int OnHand, int Reserved, int Available);

public sealed record ReceiveStockRequest(int Quantity, string? Reference);

public sealed record AdjustStockRequest(int Delta, string Reason);

public sealed record MovementDto(long Id, MovementType Type, int Quantity, string? Reference, string? Reason, Guid? UserId, DateTimeOffset CreatedAt);

public sealed class ReceiveStockRequestValidator : AbstractValidator<ReceiveStockRequest>
{
    public ReceiveStockRequestValidator()
    {
        RuleFor(x => x.Quantity).InclusiveBetween(1, 100_000);
        RuleFor(x => x.Reference).MaximumLength(100);
    }
}

public sealed class AdjustStockRequestValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockRequestValidator()
    {
        RuleFor(x => x.Delta).NotEqual(0).InclusiveBetween(-100_000, 100_000);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(200);
    }
}

/// <summary>Warehouse operations by admins: receipts, adjustments, ledger, low stock.</summary>
public sealed class InventoryService(
    IAppDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    IValidator<ReceiveStockRequest> receiveValidator,
    IValidator<AdjustStockRequest> adjustValidator)
{
    public async Task<StockDto> ReceiveAsync(Guid productId, ReceiveStockRequest request, CancellationToken ct)
    {
        await receiveValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(productId, s => s.Receive(request.Quantity, UserId(), request.Reference?.Trim(), clock.GetUtcNow()), ct);
    }

    public async Task<StockDto> AdjustAsync(Guid productId, AdjustStockRequest request, CancellationToken ct)
    {
        await adjustValidator.ValidateAndThrowAsync(request, ct);
        return await ChangeAsync(productId, s => s.Adjust(request.Delta, request.Reason, UserId(), clock.GetUtcNow()), ct);
    }

    public async Task<StockDto> GetAsync(Guid productId, CancellationToken ct) =>
        await Project().Where(s => s.ProductId == productId).Select(ToDto).SingleOrDefaultAsync(ct)
        ?? throw new NotFoundException("Product", productId);

    public async Task<PagedResult<MovementDto>> LedgerAsync(Guid productId, PageRequest paging, CancellationToken ct)
    {
        var movements = db.InventoryMovements.AsNoTracking().Where(m => m.ProductId == productId);
        var total = await movements.CountAsync(ct);
        var items = await movements.OrderByDescending(m => m.Id).Skip(paging.Skip).Take(paging.PageSize)
            .Select(m => new MovementDto(m.Id, m.Type, m.Quantity, m.Reference, m.Reason, m.UserId, m.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<MovementDto>(items, paging.Page, paging.PageSize, total);
    }

    /// <summary>Active products whose available stock is at or below the threshold — what to reorder.</summary>
    public async Task<IReadOnlyList<StockDto>> LowStockAsync(int threshold, CancellationToken ct) =>
        await Project().Where(s => s.Available <= threshold).OrderBy(s => s.Available).ThenBy(s => s.Sku).Take(200)
            .Select(ToDto).ToListAsync(ct);

    private async Task<StockDto> ChangeAsync(Guid productId, Func<StockItem, InventoryMovement> change, CancellationToken ct)
    {
        var stock = await db.StockItems.SingleOrDefaultAsync(s => s.ProductId == productId, ct)
            ?? throw new NotFoundException("Product", productId);

        db.InventoryMovements.Add(change(stock));

        // Optimistic concurrency (xmin): if a checkout reserved units between our read and write, this save
        // fails with 409 and the admin retries — the "cannot go below reserved" rule is never checked on stale data.
        await db.SaveChangesAsync(ct);
        return await GetAsync(productId, ct);
    }

    private Guid UserId() => currentUser.Id ?? throw new UnauthorizedException("Not authenticated.");

    // Filter on a class with settable properties, build the record DTO last (EF cannot filter on constructor-built records).
    private sealed class StockRow
    {
        public Guid ProductId { get; init; }
        public string Sku { get; init; } = null!;
        public string Name { get; init; } = null!;
        public int OnHand { get; init; }
        public int Reserved { get; init; }
        public int Available { get; init; }
    }

    private static readonly System.Linq.Expressions.Expression<Func<StockRow, StockDto>> ToDto = s =>
        new StockDto(s.ProductId, s.Sku, s.Name, s.OnHand, s.Reserved, s.Available);

    private IQueryable<StockRow> Project() =>
        from s in db.StockItems.AsNoTracking()
        join p in db.Products on s.ProductId equals p.Id
        where p.IsActive
        select new StockRow { ProductId = s.ProductId, Sku = p.Sku, Name = p.Name, OnHand = s.OnHand, Reserved = s.Reserved, Available = s.OnHand - s.Reserved };
}
