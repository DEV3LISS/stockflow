using Stockflow.Domain.Common;

namespace Stockflow.Domain.Orders;

public enum OrderStatus
{
    /// <summary>Stock is reserved until <see cref="Order.ReservedUntil"/>; waiting for the payment.</summary>
    PendingPayment,
    Paid,
    Shipped,

    /// <summary>Cancelled by the customer (before payment) or by an admin.</summary>
    Cancelled,

    /// <summary>Not paid in time — the reservation was released by the expiry job.</summary>
    Expired,
}

/// <summary>An order line: name, SKU and price are copied at checkout (snapshot).</summary>
public sealed record OrderLineSnapshot(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity);

public sealed class Order : Entity, IHasTimestamps
{
    public static readonly TimeSpan ReservationTime = TimeSpan.FromMinutes(15);

    private readonly List<OrderLine> _lines = [];

    private Order() { }

    public long Number { get; private set; }
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal Total { get; private set; }
    public DateTimeOffset ReservedUntil { get; private set; }

    /// <summary>Client key for checkout: a retried "Place order" returns this order instead of creating another.</summary>
    public string IdempotencyKey { get; private set; } = null!;
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public IReadOnlyCollection<OrderLine> Lines => _lines;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while the order holds reserved stock.</summary>
    public bool HoldsReservation => Status == OrderStatus.PendingPayment;

    public static Order Place(Guid customerId, IReadOnlyCollection<OrderLineSnapshot> lines, string idempotencyKey, DateTimeOffset now)
    {
        if (lines.Count == 0)
        {
            throw new DomainException("Cannot place an empty order.");
        }

        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
        {
            throw new DomainException("Each product may appear only once in an order.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw new DomainException("A valid idempotency key is required.");
        }

        var order = new Order
        {
            CustomerId = customerId,
            Status = OrderStatus.PendingPayment,
            IdempotencyKey = idempotencyKey,
            ReservedUntil = now + ReservationTime,
        };
        order._lines.AddRange(lines.Select(l => OrderLine.Create(order.Id, l)));
        order.Total = order._lines.Sum(l => l.LineTotal);
        return order;
    }

    /// <summary>
    /// Payment confirmed. Allowed only while the reservation is held. A payment for an expired or cancelled
    /// order is NOT applied here — the caller records it for refund (stock may already be sold to someone else).
    /// </summary>
    public void MarkPaid(DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidStateException($"Cannot pay for an order that is {Status}.");
        }

        Status = OrderStatus.Paid;
        PaidAt = now;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Status != OrderStatus.PendingPayment)
        {
            throw new InvalidStateException($"Only orders waiting for payment can expire (this one is {Status}).");
        }

        if (now < ReservedUntil)
        {
            throw new DomainException("The reservation has not expired yet.");
        }

        Status = OrderStatus.Expired;
        ClosedAt = now;
    }

    /// <summary>Customers may cancel only unpaid orders; admins may also cancel paid ones (stock is returned).</summary>
    public void Cancel(bool byAdmin, DateTimeOffset now)
    {
        var allowed = Status == OrderStatus.PendingPayment || (byAdmin && Status == OrderStatus.Paid);
        if (!allowed)
        {
            throw new InvalidStateException($"Cannot cancel an order that is {Status}.");
        }

        Status = OrderStatus.Cancelled;
        ClosedAt = now;
    }

    public void Ship(DateTimeOffset now)
    {
        if (Status != OrderStatus.Paid)
        {
            throw new InvalidStateException($"Cannot ship an order that is {Status}.");
        }

        Status = OrderStatus.Shipped;
        ShippedAt = now;
    }
}

public sealed class OrderLine
{
    private OrderLine() { }

    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal { get; private set; }

    internal static OrderLine Create(Guid orderId, OrderLineSnapshot s)
    {
        if (s.Quantity <= 0)
        {
            throw new DomainException("Quantity must be positive.");
        }

        return new OrderLine
        {
            OrderId = orderId,
            ProductId = s.ProductId,
            Sku = s.Sku,
            Name = s.Name,
            UnitPrice = s.UnitPrice,
            Quantity = s.Quantity,
            LineTotal = decimal.Round(s.UnitPrice * s.Quantity, 2, MidpointRounding.AwayFromZero),
        };
    }
}
