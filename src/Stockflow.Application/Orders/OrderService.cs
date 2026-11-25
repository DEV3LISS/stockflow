using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Domain.Common;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Users;

namespace Stockflow.Application.Orders;

public sealed record OrderLineDto(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record OrderDto(Guid Id, string Number, Guid CustomerId, OrderStatus Status, decimal Total,
    DateTimeOffset ReservedUntil, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, DateTimeOffset? ShippedAt,
    IReadOnlyList<OrderLineDto> Lines)
{
    public static OrderDto From(Order o) => new(o.Id, $"SF-{o.Number}", o.CustomerId, o.Status, o.Total, o.ReservedUntil,
        o.CreatedAt, o.PaidAt, o.ShippedAt,
        o.Lines.OrderBy(l => l.Name).Select(l => new OrderLineDto(l.ProductId, l.Sku, l.Name, l.UnitPrice, l.Quantity, l.LineTotal)).ToList());
}

public sealed record PlaceOrderResult(OrderDto Order, bool Replayed);

public sealed record OrderQuery(PageRequest Paging, OrderStatus? Status = null, Guid? CustomerId = null);

public sealed class OrderQueryValidator : AbstractValidator<OrderQuery>
{
    public OrderQueryValidator() => RuleFor(x => x.Paging).ValidPage();
}

/// <summary>
/// Checkout and order lifecycle. Every stock change happens inside one transaction together with the order change,
/// so stock and orders can never disagree (no "reserved for an order that does not exist").
/// </summary>
public sealed class OrderService(
    IAppDbContext db,
    IStockReservations stock,
    ICurrentUser currentUser,
    TimeProvider clock,
    StockflowMetrics metrics,
    IValidator<OrderQuery> queryValidator)
{
    /// <summary>
    /// Turns the cart into an order and reserves every line for 15 minutes — all or nothing.
    /// A retried request with the same Idempotency-Key returns the order created the first time.
    /// </summary>
    public async Task<PlaceOrderResult> PlaceAsync(string? idempotencyKey, CancellationToken ct)
    {
        var customerId = CustomerId();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 100)
        {
            throw new DomainException("Header Idempotency-Key is required (8–100 characters, e.g. a UUID).");
        }

        if (await FindByKeyAsync(customerId, idempotencyKey, ct) is { } existing)
        {
            return new PlaceOrderResult(existing, Replayed: true);
        }

        var cart = await db.Carts.Include(c => c.Items).SingleOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (cart is null || cart.Items.Count == 0)
        {
            throw new DomainException("The cart is empty.");
        }

        var productIds = cart.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsActive).ToListAsync(ct);
        if (products.Count != productIds.Count)
        {
            throw new DomainException("Some products in the cart are no longer sold. Please review the cart.");
        }

        var lines = cart.Items
            .Join(products, i => i.ProductId, p => p.Id, (i, p) => new OrderLineSnapshot(p.Id, p.Sku, p.Name, p.Price, i.Quantity))
            .ToList();

        var now = clock.GetUtcNow();
        var order = Order.Place(customerId, lines, idempotencyKey, now);

        await using var transaction = await db.BeginTransactionAsync(ct);
        var shortages = await stock.TryReserveAsync(ToStock(order), ct);
        if (shortages.Count > 0)
        {
            await transaction.RollbackAsync(ct); // partial reservations of other lines are undone too
            metrics.OutOfStock();
            throw new InsufficientStockException(products.Where(p => shortages.Contains(p.Id)).Select(p => p.Sku).ToList());
        }

        db.Orders.Add(order);
        cart.Clear();

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same Idempotency-Key raced in from a parallel retry; the unique index let one order win.
            await transaction.RollbackAsync(ct);
            if (await FindByKeyAsync(customerId, idempotencyKey, ct) is { } winner)
            {
                return new PlaceOrderResult(winner, Replayed: true);
            }

            throw;
        }

        metrics.OrderPlaced();
        return new PlaceOrderResult(await GetAsync(order.Id, ct), Replayed: false);
    }

    /// <summary>
    /// A customer sees only their own orders. For someone else's order we answer 404, not 403:
    /// "forbidden" would confirm that the order id exists (information leak / IDOR probing).
    /// </summary>
    public async Task<OrderDto> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !CanSee(order))
        {
            throw new NotFoundException("Order", id);
        }

        return OrderDto.From(order);
    }

    public async Task<PagedResult<OrderDto>> ListAsync(OrderQuery query, CancellationToken ct)
    {
        await queryValidator.ValidateAndThrowAsync(query, ct);
        var orders = db.Orders.AsNoTracking().Include(o => o.Lines).AsQueryable();

        if (currentUser.Role != UserRole.Admin)
        {
            var me = CustomerId();
            orders = orders.Where(o => o.CustomerId == me); // customers: own orders only, whatever the filter says
        }
        else if (query.CustomerId is not null)
        {
            orders = orders.Where(o => o.CustomerId == query.CustomerId);
        }

        if (query.Status is not null)
        {
            orders = orders.Where(o => o.Status == query.Status);
        }

        var total = await orders.CountAsync(ct);
        var page = await orders.OrderByDescending(o => o.Number).Skip(query.Paging.Skip).Take(query.Paging.PageSize).ToListAsync(ct);
        return new PagedResult<OrderDto>(page.Select(OrderDto.From).ToList(), query.Paging.Page, query.Paging.PageSize, total);
    }

    /// <summary>Customer: own unpaid order → reservation released. Admin: also paid orders → units returned to stock.</summary>
    public async Task<OrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var isAdmin = currentUser.Role == UserRole.Admin;
        await using var transaction = await db.BeginTransactionAsync(ct);
        var order = await db.LockOrderAsync(id, ct);
        if (order is null || !CanSee(order))
        {
            throw new NotFoundException("Order", id);
        }

        var wasPending = order.HoldsReservation;
        var wasPaid = order.Status == OrderStatus.Paid;
        var now = clock.GetUtcNow();
        order.Cancel(isAdmin, now);

        if (wasPending)
        {
            await stock.ReleaseAsync(ToStock(order), ct);
        }
        else if (wasPaid)
        {
            await stock.ReturnAsync(ToStock(order), ct);
            foreach (var line in order.Lines)
            {
                db.InventoryMovements.Add(InventoryMovement.Create(line.ProductId, MovementType.Return, line.Quantity,
                    $"SF-{order.Number}", "Paid order cancelled", currentUser.Id, now));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return OrderDto.From(order);
    }

    public async Task<OrderDto> ShipAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct)
            ?? throw new NotFoundException("Order", id);
        order.Ship(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return OrderDto.From(order);
    }

    internal static IReadOnlyList<StockLine> ToStock(Order order) =>
        order.Lines.Select(l => new StockLine(l.ProductId, l.Quantity)).ToList();

    private async Task<OrderDto?> FindByKeyAsync(Guid customerId, string key, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.CustomerId == customerId && o.IdempotencyKey == key, ct);
        return order is null ? null : OrderDto.From(order);
    }

    private bool CanSee(Order order) => currentUser.Role == UserRole.Admin || order.CustomerId == currentUser.Id;

    private Guid CustomerId() => currentUser.Id ?? throw new UnauthorizedException("Not authenticated.");
}
