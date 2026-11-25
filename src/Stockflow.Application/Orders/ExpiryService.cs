using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;

namespace Stockflow.Application.Orders;

/// <summary>
/// Releases stock held by orders that were not paid in time. Called by a background worker every few seconds.
/// Each batch is one transaction: lock due orders (skipping those another instance is handling), release, expire.
/// </summary>
public sealed class ExpiryService(IAppDbContext db, IStockReservations stock, TimeProvider clock, StockflowMetrics metrics)
{
    public const int BatchSize = 50;

    /// <returns>Number of orders expired in this batch.</returns>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.BeginTransactionAsync(ct);
        var due = await db.LockExpiredOrdersAsync(now, BatchSize, ct);

        foreach (var order in due)
        {
            await stock.ReleaseAsync(OrderService.ToStock(order), ct);
            order.Expire(now);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (due.Count > 0)
        {
            metrics.OrdersExpired(due.Count);
        }

        return due.Count;
    }
}
