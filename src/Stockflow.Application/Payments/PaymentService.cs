using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stockflow.Application.Abstractions;
using Stockflow.Application.Common;
using Stockflow.Application.Orders;
using Stockflow.Domain.Common;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;

namespace Stockflow.Application.Payments;

public sealed record StartPaymentResult(Guid PaymentId, string ProviderPaymentId, string PaymentUrl, decimal Amount);

/// <summary>A verified notification from the payment provider (signature checked in the Api layer).</summary>
public sealed record PaymentWebhook(string EventId, string Type, string ProviderPaymentId);

public enum WebhookOutcome
{
    Applied,
    Duplicate,
    IgnoredOutOfOrder,
    UnknownPayment,
    PaidTooLate,
}

/// <summary>
/// Payments: start at the provider, then apply the provider's webhooks.
/// Webhooks are the source of truth for money; they may be delivered twice, late or out of order.
/// </summary>
public sealed partial class PaymentService(
    IAppDbContext db,
    IStockReservations stock,
    IPaymentProvider provider,
    ICurrentUser currentUser,
    TimeProvider clock,
    StockflowMetrics metrics,
    ILogger<PaymentService> logger)
{
    public const string SucceededEvent = "payment.succeeded";
    public const string FailedEvent = "payment.failed";

    public async Task<StartPaymentResult> StartAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || order.CustomerId != currentUser.Id)
        {
            throw new NotFoundException("Order", orderId);
        }

        if (!order.HoldsReservation || order.ReservedUntil <= clock.GetUtcNow())
        {
            throw new DomainException("This order can no longer be paid. Please place a new order.");
        }

        var created = await provider.CreatePaymentAsync(order.Id, order.Total, ct);
        var payment = Payment.Start(order.Id, created.ProviderPaymentId, order.Total);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return new StartPaymentResult(payment.Id, created.ProviderPaymentId, created.PaymentUrl, order.Total);
    }

    public async Task<WebhookOutcome> HandleWebhookAsync(PaymentWebhook webhook, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.BeginTransactionAsync(ct);

        // 1. Exactly-once on our side: remember the event id. A second delivery fails the unique key.
        if (await db.ProcessedWebhookEvents.AnyAsync(e => e.EventId == webhook.EventId, ct))
        {
            metrics.Webhook("duplicate");
            return WebhookOutcome.Duplicate;
        }

        db.ProcessedWebhookEvents.Add(ProcessedWebhookEvent.Create(webhook.EventId, webhook.Type, now));

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.ProviderPaymentId == webhook.ProviderPaymentId, ct);
        if (payment is null)
        {
            LogUnknownPayment(logger, webhook.ProviderPaymentId);
            await SaveAsync(transaction, ct);
            return Count(WebhookOutcome.UnknownPayment);
        }

        // 2. Serialise with the expiry job and with other webhooks for the same order.
        var order = await db.LockOrderAsync(payment.OrderId, ct) ?? throw new NotFoundException("Order", payment.OrderId);

        if (payment.IsFinal)
        {
            // e.g. "failed" arriving after "succeeded": terminal states never change.
            await SaveAsync(transaction, ct);
            return Count(WebhookOutcome.IgnoredOutOfOrder);
        }

        var outcome = WebhookOutcome.Applied;
        if (webhook.Type == SucceededEvent)
        {
            if (order.HoldsReservation)
            {
                await stock.CommitSaleAsync(OrderService.ToStock(order), ct);
                foreach (var line in order.Lines)
                {
                    db.InventoryMovements.Add(InventoryMovement.Create(line.ProductId, MovementType.Sale, -line.Quantity,
                        $"SF-{order.Number}", null, null, now));
                }

                order.MarkPaid(now);
                payment.Succeed();
                metrics.OrderPaid(order.Total);
            }
            else
            {
                // Money came for an order that already expired/was cancelled — its stock may be sold to someone else.
                // Never silently "un-expire": flag for a refund and let a human decide.
                payment.FlagForRefund();
                LogPaidTooLate(logger, order.Id, order.Status);
                outcome = WebhookOutcome.PaidTooLate;
            }
        }
        else if (webhook.Type == FailedEvent)
        {
            payment.Fail(); // the order keeps its reservation until expiry; the customer may try again
        }
        else
        {
            throw new DomainException($"Unknown webhook type '{webhook.Type}'.");
        }

        await SaveAsync(transaction, ct);
        return Count(outcome);
    }

    private async Task SaveAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private WebhookOutcome Count(WebhookOutcome outcome)
    {
        metrics.Webhook(outcome.ToString());
        return outcome;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook for unknown payment {ProviderPaymentId}")]
    private static partial void LogUnknownPayment(ILogger logger, string providerPaymentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment succeeded for order {OrderId} that is {Status} — flagged for refund")]
    private static partial void LogPaidTooLate(ILogger logger, Guid orderId, OrderStatus status);
}
