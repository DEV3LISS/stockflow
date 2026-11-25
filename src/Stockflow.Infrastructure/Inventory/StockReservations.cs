using Microsoft.EntityFrameworkCore;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Infrastructure.Persistence;

namespace Stockflow.Infrastructure.Inventory;

public enum ReservationStrategy
{
    /// <summary>One conditional UPDATE per line (default).</summary>
    Atomic,

    /// <summary>SELECT … FOR UPDATE, check in C#, UPDATE.</summary>
    Pessimistic,

    /// <summary>Read with version (xmin), UPDATE … WHERE xmin = old, retry on conflict.</summary>
    Optimistic,
}

public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    /// <summary>Kept configurable so the three strategies can be load-tested against each other (docs/performance.md).</summary>
    public ReservationStrategy ReservationStrategy { get; init; } = ReservationStrategy.Atomic;
}

/// <summary>
/// Shared part: releasing, selling and returning stock are always done while the ORDER row is locked,
/// so they never compete with each other for the same order; plain atomic UPDATEs are enough.
/// Lines are always processed in product-id order — every transaction locks stock rows in the same order,
/// which rules out deadlocks between two multi-line checkouts.
/// </summary>
public abstract class StockReservationsBase(AppDbContext db) : IStockReservations
{
    protected AppDbContext Db { get; } = db;

    public abstract Task<IReadOnlyList<Guid>> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct);

    public async Task ReleaseAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        foreach (var line in Ordered(lines))
        {
            await Db.Database.ExecuteSqlAsync(
                $"UPDATE stock_items SET reserved = reserved - {line.Quantity}, updated_at = now() WHERE product_id = {line.ProductId}", ct);
        }
    }

    public async Task CommitSaleAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        foreach (var line in Ordered(lines))
        {
            await Db.Database.ExecuteSqlAsync(
                $"UPDATE stock_items SET on_hand = on_hand - {line.Quantity}, reserved = reserved - {line.Quantity}, updated_at = now() WHERE product_id = {line.ProductId}", ct);
        }
    }

    public async Task ReturnAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        foreach (var line in Ordered(lines))
        {
            await Db.Database.ExecuteSqlAsync(
                $"UPDATE stock_items SET on_hand = on_hand + {line.Quantity}, updated_at = now() WHERE product_id = {line.ProductId}", ct);
        }
    }

    protected static IEnumerable<StockLine> Ordered(IReadOnlyList<StockLine> lines) => lines.OrderBy(l => l.ProductId);
}

/// <summary>
/// The default: <c>UPDATE stock_items SET reserved = reserved + q WHERE product_id = :id AND on_hand - reserved &gt;= q</c>.
/// Check and change happen in ONE statement inside the database. Concurrent buyers queue on the row lock
/// for microseconds; the WHERE is re-evaluated against the latest committed row, so the last unit can only be
/// taken once. 0 rows updated = not enough stock. No round-trip between "check" and "write" → no race window.
/// </summary>
public sealed class AtomicStockReservations(AppDbContext db) : StockReservationsBase(db)
{
    public override async Task<IReadOnlyList<Guid>> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        var shortages = new List<Guid>();
        foreach (var line in Ordered(lines))
        {
            var updated = await Db.Database.ExecuteSqlAsync($"""
                UPDATE stock_items SET reserved = reserved + {line.Quantity}, updated_at = now()
                WHERE product_id = {line.ProductId} AND on_hand - reserved >= {line.Quantity}
                """, ct);
            if (updated == 0)
            {
                shortages.Add(line.ProductId);
            }
        }

        return shortages;
    }
}

/// <summary>
/// Pessimistic: lock the stock rows first (FOR UPDATE), decide in C#, then write. Correct, but the row lock is
/// held across two extra network round-trips, so under contention buyers wait longer (measured in docs/performance.md).
/// </summary>
public sealed class PessimisticStockReservations(AppDbContext db) : StockReservationsBase(db)
{
    public override async Task<IReadOnlyList<Guid>> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.ProductId).ToArray();
        var rows = await Db.Database.SqlQuery<StockRow>($"""
            SELECT product_id, on_hand, reserved FROM stock_items
            WHERE product_id = ANY({ids}) ORDER BY product_id FOR UPDATE
            """).ToListAsync(ct);

        var shortages = lines
            .Where(l => rows.SingleOrDefault(r => r.ProductId == l.ProductId) is not { } r || r.OnHand - r.Reserved < l.Quantity)
            .Select(l => l.ProductId)
            .ToList();

        if (shortages.Count == 0)
        {
            foreach (var line in Ordered(lines))
            {
                await Db.Database.ExecuteSqlAsync(
                    $"UPDATE stock_items SET reserved = reserved + {line.Quantity}, updated_at = now() WHERE product_id = {line.ProductId}", ct);
            }
        }

        return shortages;
    }

    private sealed class StockRow
    {
        public Guid ProductId { get; init; }
        public int OnHand { get; init; }
        public int Reserved { get; init; }
    }
}

/// <summary>
/// Optimistic: read stock and its version (xmin) without locking, then UPDATE only if the version is unchanged;
/// otherwise somebody else won — re-read and retry. Great when conflicts are rare, poor in a flash sale where
/// almost every attempt conflicts (retries burn database time; measured in docs/performance.md).
/// </summary>
public sealed class OptimisticStockReservations(AppDbContext db) : StockReservationsBase(db)
{
    public const int MaxAttempts = 10;

    public override async Task<IReadOnlyList<Guid>> TryReserveAsync(IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        var shortages = new List<Guid>();
        foreach (var line in Ordered(lines))
        {
            var reserved = false;
            for (var attempt = 1; attempt <= MaxAttempts && !reserved; attempt++)
            {
                var row = await Db.Database.SqlQuery<VersionedRow>($"""
                    SELECT on_hand, reserved, xmin::text::bigint AS version FROM stock_items WHERE product_id = {line.ProductId}
                    """).SingleOrDefaultAsync(ct);

                if (row is null || row.OnHand - row.Reserved < line.Quantity)
                {
                    shortages.Add(line.ProductId);
                    break;
                }

                var updated = await Db.Database.ExecuteSqlAsync($"""
                    UPDATE stock_items SET reserved = reserved + {line.Quantity}, updated_at = now()
                    WHERE product_id = {line.ProductId} AND xmin::text::bigint = {row.Version}
                    """, ct);
                reserved = updated == 1;
            }

            if (!reserved && !shortages.Contains(line.ProductId))
            {
                throw new ConflictException("The product is in very high demand right now. Please try again.");
            }
        }

        return shortages;
    }

    private sealed class VersionedRow
    {
        public int OnHand { get; init; }
        public int Reserved { get; init; }
        public long Version { get; init; }
    }
}
