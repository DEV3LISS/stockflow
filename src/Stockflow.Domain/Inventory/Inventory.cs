using Stockflow.Domain.Common;

namespace Stockflow.Domain.Inventory;

/// <summary>
/// Stock level of one product. <b>OnHand</b> = physically in the warehouse; <b>Reserved</b> = held by unpaid orders.
/// What can still be sold is <see cref="Available"/>. The same invariants are CHECK constraints in the database.
/// </summary>
/// <remarks>
/// Reservations during checkout do NOT go through these methods: they are done by a single atomic SQL statement
/// (see IStockReservations and ADR-004) because that is where hundreds of buyers compete for the same row.
/// These methods cover the low-contention admin operations, protected by optimistic concurrency.
/// </remarks>
public sealed class StockItem
{
    private StockItem() { }

    public Guid ProductId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public int Available => OnHand - Reserved;

    public static StockItem For(Guid productId, DateTimeOffset now) => new() { ProductId = productId, UpdatedAt = now };

    /// <summary>Goods arrived from a supplier. <paramref name="userId"/> is null for system imports.</summary>
    public InventoryMovement Receive(int quantity, Guid? userId, string? reference, DateTimeOffset now)
    {
        if (quantity is <= 0 or > 100_000)
        {
            throw new DomainException("Received quantity must be between 1 and 100 000.");
        }

        OnHand += quantity;
        UpdatedAt = now;
        return InventoryMovement.Create(ProductId, MovementType.Receipt, quantity, reference, null, userId, now);
    }

    /// <summary>Correction after a count, damage, loss. Cannot take away units that are reserved by orders.</summary>
    public InventoryMovement Adjust(int delta, string reason, Guid userId, DateTimeOffset now)
    {
        if (delta == 0)
        {
            throw new DomainException("Adjustment cannot be zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A reason is required for a stock adjustment.");
        }

        if (OnHand + delta < Reserved)
        {
            throw new DomainException(
                $"Cannot reduce stock to {OnHand + delta}: {Reserved} units are reserved by unpaid orders.");
        }

        OnHand += delta;
        UpdatedAt = now;
        return InventoryMovement.Create(ProductId, MovementType.Adjustment, delta, null, reason.Trim(), userId, now);
    }
}

public enum MovementType
{
    Receipt,
    Adjustment,
    Sale,
    Return,
}

/// <summary>
/// Append-only ledger of on-hand changes, like an accounting journal: OnHand of a product always equals
/// the sum of its movements. Answers "where did these 5 units go?" and lets us rebuild or verify stock.
/// </summary>
public sealed class InventoryMovement
{
    private InventoryMovement() { }

    public long Id { get; private set; }
    public Guid ProductId { get; private set; }
    public MovementType Type { get; private set; }

    /// <summary>Signed change of OnHand (+ receipt/return, − sale, ± adjustment).</summary>
    public int Quantity { get; private set; }

    /// <summary>Order id for sales/returns, delivery note for receipts.</summary>
    public string? Reference { get; private set; }
    public string? Reason { get; private set; }
    public Guid? UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static InventoryMovement Create(Guid productId, MovementType type, int quantity, string? reference,
        string? reason, Guid? userId, DateTimeOffset now) => new()
    {
        ProductId = productId,
        Type = type,
        Quantity = quantity,
        Reference = reference,
        Reason = reason,
        UserId = userId,
        CreatedAt = now,
    };
}
