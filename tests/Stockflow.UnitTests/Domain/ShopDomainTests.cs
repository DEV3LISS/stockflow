using Stockflow.Domain.Carts;
using Stockflow.Domain.Common;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;

namespace Stockflow.UnitTests.Domain;

public class StockItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Receipt_increases_on_hand_and_produces_a_ledger_entry()
    {
        var stock = StockItem.For(Guid.NewGuid(), Now);

        var movement = stock.Receive(10, Guid.NewGuid(), "DN-1", Now);

        stock.OnHand.ShouldBe(10);
        stock.Available.ShouldBe(10);
        movement.Type.ShouldBe(MovementType.Receipt);
        movement.Quantity.ShouldBe(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Receipt_quantity_must_be_sane(int quantity) =>
        Should.Throw<DomainException>(() => StockItem.For(Guid.NewGuid(), Now).Receive(quantity, null, null, Now));

    [Fact]
    public void Adjustment_needs_a_reason_and_cannot_be_zero()
    {
        var stock = StockItem.For(Guid.NewGuid(), Now);
        stock.Receive(5, null, null, Now);

        Should.Throw<DomainException>(() => stock.Adjust(-1, " ", Guid.NewGuid(), Now));
        Should.Throw<DomainException>(() => stock.Adjust(0, "count", Guid.NewGuid(), Now));
        stock.Adjust(-2, "damaged", Guid.NewGuid(), Now).Quantity.ShouldBe(-2);
        stock.OnHand.ShouldBe(3);
    }

    [Fact]
    public void Adjustment_cannot_go_below_zero() =>
        Should.Throw<DomainException>(() => StockItem.For(Guid.NewGuid(), Now).Adjust(-1, "count", Guid.NewGuid(), Now));
}

public class CartTests
{
    [Fact]
    public void Set_quantity_adds_updates_and_removes()
    {
        var cart = Cart.For(Guid.NewGuid());
        var product = Guid.NewGuid();

        cart.SetQuantity(product, 2);
        cart.SetQuantity(product, 5);
        cart.Items.Single().Quantity.ShouldBe(5);

        cart.SetQuantity(product, 0);
        cart.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void Quantity_is_limited(int quantity) =>
        Should.Throw<DomainException>(() => Cart.For(Guid.NewGuid()).SetQuantity(Guid.NewGuid(), quantity));
}

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => Order.Place(Guid.NewGuid(),
    [
        new OrderLineSnapshot(Guid.NewGuid(), "SKU-1", "Phone", 999.99m, 2),
        new OrderLineSnapshot(Guid.NewGuid(), "SKU-2", "Case", 10.01m, 3),
    ], "key-12345678", Now);

    [Fact]
    public void Placing_snapshots_prices_and_reserves_for_15_minutes()
    {
        var order = NewOrder();

        order.Status.ShouldBe(OrderStatus.PendingPayment);
        order.Total.ShouldBe(2030.01m); // 2 × 999.99 + 3 × 10.01
        order.ReservedUntil.ShouldBe(Now.AddMinutes(15));
        order.HoldsReservation.ShouldBeTrue();
    }

    [Fact]
    public void Duplicate_products_and_empty_orders_are_rejected()
    {
        var id = Guid.NewGuid();
        Should.Throw<DomainException>(() => Order.Place(Guid.NewGuid(), [], "key-12345678", Now));
        Should.Throw<DomainException>(() => Order.Place(Guid.NewGuid(),
            [new OrderLineSnapshot(id, "A", "A", 1m, 1), new OrderLineSnapshot(id, "A", "A", 1m, 1)], "key-12345678", Now));
    }

    [Fact]
    public void Paid_order_can_be_shipped_but_not_paid_again()
    {
        var order = NewOrder();
        order.MarkPaid(Now);
        order.Ship(Now);

        order.Status.ShouldBe(OrderStatus.Shipped);
        Should.Throw<InvalidStateException>(() => order.MarkPaid(Now));
    }

    [Fact]
    public void Expiry_only_after_the_deadline_and_then_payment_is_refused()
    {
        var order = NewOrder();

        Should.Throw<DomainException>(() => order.Expire(Now.AddMinutes(14)));
        order.Expire(Now.AddMinutes(15));

        order.Status.ShouldBe(OrderStatus.Expired);
        order.HoldsReservation.ShouldBeFalse();
        Should.Throw<InvalidStateException>(() => order.MarkPaid(Now.AddMinutes(16)));
    }

    [Fact]
    public void Customer_cannot_cancel_a_paid_order_but_admin_can()
    {
        var order = NewOrder();
        order.MarkPaid(Now);

        Should.Throw<InvalidStateException>(() => order.Cancel(byAdmin: false, Now));
        order.Cancel(byAdmin: true, Now);
        order.Status.ShouldBe(OrderStatus.Cancelled);
    }
}

public class PaymentTests
{
    [Fact]
    public void Terminal_states_never_change()
    {
        var payment = Payment.Start(Guid.NewGuid(), "pay_1", 100m);
        payment.Succeed();

        payment.IsFinal.ShouldBeTrue();
        Should.Throw<InvalidStateException>(payment.Fail);
        Should.Throw<InvalidStateException>(payment.FlagForRefund);
    }
}
