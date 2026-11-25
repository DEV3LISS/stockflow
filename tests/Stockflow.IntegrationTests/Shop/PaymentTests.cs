using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockflow.Application.Common;
using Stockflow.Application.Inventory;
using Stockflow.Application.Payments;
using Stockflow.Domain.Inventory;
using Stockflow.Domain.Orders;
using Stockflow.Domain.Payments;
using Stockflow.Infrastructure.Persistence;
using Stockflow.IntegrationTests.Infrastructure;

namespace Stockflow.IntegrationTests.Shop;

public class PaymentTests(ApiFactory api) : ApiTest(api), IAsyncLifetime
{
    private HttpClient _admin = null!;
    private ShopData _shop = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        await InitializeAsync();
        _admin = await Api.CreateAdminClientAsync();
        _shop = new ShopData(Api, _admin);
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _admin.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<string> OutcomeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("outcome").GetString()!;
    }

    private async Task<PaymentStatus> PaymentStatusAsync(string providerPaymentId)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Payments
            .Where(p => p.ProviderPaymentId == providerPaymentId).Select(p => p.Status).SingleAsync();
    }

    [Fact]
    public async Task Successful_payment_turns_the_reservation_into_a_sale()
    {
        var product = await _shop.ProductAsync(stock: 5);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 2);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);
        payment.PaymentUrl.ShouldContain(payment.ProviderPaymentId);

        (await OutcomeAsync(await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent))).ShouldBe("Applied");

        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
        var stock = await _shop.StockAsync(product.Id);
        stock.OnHand.ShouldBe(3);
        stock.Reserved.ShouldBe(0);
        var ledger = await _admin.GetFromJsonAsync<PagedResult<MovementDto>>($"/api/v1/inventory/{product.Id}/movements", Json.Options);
        ledger!.Items[0].Type.ShouldBe(MovementType.Sale);
        ledger.Items[0].Quantity.ShouldBe(-2);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Same_webhook_delivered_three_times_is_applied_once()
    {
        var product = await _shop.ProductAsync(stock: 5);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);
        var eventId = "evt_" + Guid.NewGuid().ToString("N");

        var outcomes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            outcomes.Add(await OutcomeAsync(await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent, eventId)));
        }

        outcomes.ShouldBe(["Applied", "Duplicate", "Duplicate"]);
        (await _shop.StockAsync(product.Id)).OnHand.ShouldBe(4); // sold once, not three times
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Late_failed_event_does_not_undo_a_success()
    {
        var product = await _shop.ProductAsync(stock: 5);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);

        await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent);
        (await OutcomeAsync(await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.FailedEvent))).ShouldBe("IgnoredOutOfOrder");

        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
        (await PaymentStatusAsync(payment.ProviderPaymentId)).ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Failed_payment_keeps_the_reservation_so_the_customer_can_retry()
    {
        var product = await _shop.ProductAsync(stock: 1);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
        var first = await ShopData.StartPaymentAsync(customer, order.Id);

        await _shop.WebhookAsync(first.ProviderPaymentId, PaymentService.FailedEvent);
        (await _shop.StockAsync(product.Id)).Reserved.ShouldBe(1);

        var second = await ShopData.StartPaymentAsync(customer, order.Id);
        await _shop.WebhookAsync(second.ProviderPaymentId, PaymentService.SucceededEvent);
        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("old")]
    public async Task Forged_or_replayed_webhooks_are_rejected(string attack)
    {
        var product = await _shop.ProductAsync(stock: 5);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);

        var response = attack == "wrong-secret"
            ? await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent, secret: "attacker-secret-attacker-secret-attacker")
            : await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent, signedAt: Api.Clock.GetUtcNow().AddMinutes(-10));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.PendingPayment);
    }

    [Fact]
    public async Task Unpaid_order_expires_after_15_minutes_and_stock_comes_back()
    {
        var product = await _shop.ProductAsync(stock: 2);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 2);

        Api.Clock.Advance(TimeSpan.FromMinutes(14));
        (await _shop.ExpireDueAsync()).ShouldBe(0);

        Api.Clock.Advance(TimeSpan.FromMinutes(2));
        (await _shop.ExpireDueAsync()).ShouldBe(1);

        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Expired);
        (await _shop.StockAsync(product.Id)).Available.ShouldBe(2);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Payment_arriving_after_expiry_is_flagged_for_refund_not_applied()
    {
        var product = await _shop.ProductAsync(stock: 1);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);
        using var other = await _shop.CustomerAsync(); // logged in before the clock jumps (JWTs are validated on real time)

        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        await _shop.ExpireDueAsync();
        // Meanwhile somebody else buys the unit that came back.
        await ShopData.PlaceOrderAsync(other, product.Id, 1);

        (await OutcomeAsync(await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent))).ShouldBe("PaidTooLate");

        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Expired);
        (await PaymentStatusAsync(payment.ProviderPaymentId)).ShouldBe(PaymentStatus.NeedsRefund);
        await _shop.AssertInventoryConsistentAsync(product.Id); // nothing sold twice
    }

    [Fact]
    public async Task Payment_and_expiry_racing_at_the_deadline_never_leave_a_paid_but_released_order()
    {
        for (var round = 0; round < 10; round++)
        {
            Api.Clock.Reset();
            var product = await _shop.ProductAsync(stock: 1);
            using var customer = await _shop.CustomerAsync();
            var order = await ShopData.PlaceOrderAsync(customer, product.Id, 1);
            var payment = await ShopData.StartPaymentAsync(customer, order.Id);
            Api.Clock.Advance(Order.ReservationTime);

            await Task.WhenAll(
                _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent),
                _shop.ExpireDueAsync());

            var final = await _shop.OrderAsync(order.Id);
            var stock = await _shop.StockAsync(product.Id);
            if (final.Status == OrderStatus.Paid)
            {
                stock.OnHand.ShouldBe(0); // sold
                (await PaymentStatusAsync(payment.ProviderPaymentId)).ShouldBe(PaymentStatus.Succeeded);
            }
            else
            {
                final.Status.ShouldBe(OrderStatus.Expired);
                stock.Available.ShouldBe(1); // released
                (await PaymentStatusAsync(payment.ProviderPaymentId)).ShouldBe(PaymentStatus.NeedsRefund);
            }

            await _shop.AssertInventoryConsistentAsync(product.Id);
        }
    }

    [Fact]
    public async Task Expiry_workers_on_several_instances_expire_each_order_once()
    {
        var product = await _shop.ProductAsync(stock: 30);
        for (var i = 0; i < 15; i++)
        {
            using var c = await _shop.CustomerAsync();
            await ShopData.PlaceOrderAsync(c, product.Id, 2);
        }

        Api.Clock.Advance(TimeSpan.FromMinutes(20));
        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _shop.ExpireDueAsync()));

        counts.Sum().ShouldBe(15);
        (await _shop.StockAsync(product.Id)).Available.ShouldBe(30);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Admin_cancelling_a_paid_order_returns_the_units()
    {
        var product = await _shop.ProductAsync(stock: 3);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 2);
        var payment = await ShopData.StartPaymentAsync(customer, order.Id);
        await _shop.WebhookAsync(payment.ProviderPaymentId, PaymentService.SucceededEvent);

        await (await customer.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).ShouldBeProblemAsync(HttpStatusCode.Conflict);
        (await _admin.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).EnsureSuccessStatusCode();

        (await _shop.StockAsync(product.Id)).OnHand.ShouldBe(3);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }
}
