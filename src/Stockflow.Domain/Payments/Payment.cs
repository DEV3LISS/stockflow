using Stockflow.Domain.Common;

namespace Stockflow.Domain.Payments;

public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,

    /// <summary>Money arrived for an order that had already expired or been cancelled — must be refunded by a human.</summary>
    NeedsRefund,
}

/// <summary>
/// A payment attempt at the external provider. Status changes come only from verified webhooks.
/// Terminal states never change back: a late "failed" event cannot undo a "succeeded" one (events can arrive out of order).
/// </summary>
public sealed class Payment : Entity, IHasTimestamps
{
    private Payment() { }

    public Guid OrderId { get; private set; }

    /// <summary>The provider's id ("pay_…"). Unique.</summary>
    public string ProviderPaymentId { get; private set; } = null!;
    public decimal Amount { get; private set; }
    public PaymentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsFinal => Status != PaymentStatus.Pending;

    public static Payment Start(Guid orderId, string providerPaymentId, decimal amount) => new()
    {
        OrderId = orderId,
        ProviderPaymentId = providerPaymentId,
        Amount = amount,
        Status = PaymentStatus.Pending,
    };

    public void Succeed() => Finish(PaymentStatus.Succeeded);

    public void Fail() => Finish(PaymentStatus.Failed);

    public void FlagForRefund() => Finish(PaymentStatus.NeedsRefund);

    private void Finish(PaymentStatus status)
    {
        if (IsFinal)
        {
            throw new InvalidStateException($"Payment is already {Status}.");
        }

        Status = status;
    }
}

/// <summary>
/// Every webhook event id we have processed. The provider retries deliveries (at-least-once), so the same
/// event may arrive many times; the unique key makes processing exactly-once on our side.
/// </summary>
public sealed class ProcessedWebhookEvent
{
    private ProcessedWebhookEvent() { }

    public string EventId { get; private set; } = null!;
    public string Type { get; private set; } = null!;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedWebhookEvent Create(string eventId, string type, DateTimeOffset now) =>
        new() { EventId = eventId, Type = type, ProcessedAt = now };
}
