using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Stockflow.Application.Carts;
using Stockflow.Application.Orders;
using Stockflow.Domain.Orders;
using Stockflow.IntegrationTests.Infrastructure;

namespace Stockflow.IntegrationTests.Shop;

public class CheckoutTests(ApiFactory api) : ApiTest(api), IAsyncLifetime
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

    [Fact]
    public async Task Checkout_reserves_stock_clears_the_cart_and_snapshots_prices()
    {
        var product = await _shop.ProductAsync(stock: 5, price: 1500m);
        using var customer = await _shop.CustomerAsync();
        await ShopData.AddToCartAsync(customer, product.Id, 2);

        var response = await ShopData.CheckoutAsync(customer);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>(Json.Options))!;
        order.Status.ShouldBe(OrderStatus.PendingPayment);
        order.Total.ShouldBe(3000m);
        order.Number.ShouldStartWith("SF-");

        var stock = await _shop.StockAsync(product.Id);
        stock.OnHand.ShouldBe(5);
        stock.Reserved.ShouldBe(2);
        stock.Available.ShouldBe(3);
        (await customer.GetFromJsonAsync<CartDto>("/api/v1/cart", Json.Options))!.Lines.ShouldBeEmpty();
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Retried_checkout_with_the_same_key_returns_the_same_order()
    {
        var product = await _shop.ProductAsync(stock: 5);
        using var customer = await _shop.CustomerAsync();
        await ShopData.AddToCartAsync(customer, product.Id, 1);
        var key = Guid.NewGuid().ToString();

        var first = await ShopData.CheckoutAsync(customer, key);
        var retry = await ShopData.CheckoutAsync(customer, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<OrderDto>(Json.Options))!.Id
            .ShouldBe((await first.Content.ReadFromJsonAsync<OrderDto>(Json.Options))!.Id);
        (await _shop.StockAsync(product.Id)).Reserved.ShouldBe(1);
    }

    [Fact]
    public async Task Multi_line_checkout_is_all_or_nothing()
    {
        var plenty = await _shop.ProductAsync(stock: 10);
        var scarce = await _shop.ProductAsync(stock: 1);
        using var customer = await _shop.CustomerAsync();
        await ShopData.AddToCartAsync(customer, plenty.Id, 3);
        await ShopData.AddToCartAsync(customer, scarce.Id, 2);

        var response = await ShopData.CheckoutAsync(customer);

        var problem = await response.ShouldBeProblemAsync(HttpStatusCode.Conflict);
        problem.GetProperty("outOfStock").EnumerateArray().Select(e => e.GetString()).ShouldBe([scarce.Sku]);
        // The plentiful line was reserved first — and rolled back with the transaction.
        (await _shop.StockAsync(plenty.Id)).Reserved.ShouldBe(0);
        (await _shop.StockAsync(scarce.Id)).Reserved.ShouldBe(0);
    }

    [Theory]
    [InlineData("Atomic")]
    [InlineData("Pessimistic")]
    [InlineData("Optimistic")]
    public async Task Hundred_buyers_for_the_last_unit_produce_exactly_one_order(string strategy)
    {
        using var shop = Api.WithWebHostBuilder(b => b.UseSetting("Inventory:ReservationStrategy", strategy));
        var product = await _shop.ProductAsync(stock: 1);
        var buyers = await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
        {
            var c = await _shop.CustomerAsync();
            await ShopData.AddToCartAsync(c, product.Id, 1);
            return c;
        }));

        // All checkouts go to the host configured with this strategy, at the same moment.
        var responses = await Task.WhenAll(buyers.Select(async b =>
        {
            using var client = shop.CreateClient();
            client.DefaultRequestHeaders.Authorization = b.DefaultRequestHeaders.Authorization;
            return await ShopData.CheckoutAsync(client);
        }));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(99);
        var stock = await _shop.StockAsync(product.Id);
        stock.Reserved.ShouldBe(1);
        stock.Available.ShouldBe(0);
        await _shop.AssertInventoryConsistentAsync(product.Id);
        foreach (var b in buyers)
        {
            b.Dispose();
        }
    }

    [Fact]
    public async Task Flash_sale_sells_exactly_the_stock()
    {
        var product = await _shop.ProductAsync(stock: 10);
        var buyers = await Task.WhenAll(Enumerable.Range(0, 40).Select(async _ =>
        {
            var c = await _shop.CustomerAsync();
            await ShopData.AddToCartAsync(c, product.Id, 1);
            return c;
        }));

        var responses = await Task.WhenAll(buyers.Select(b => ShopData.CheckoutAsync(b)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(10);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(30);
        (await _shop.StockAsync(product.Id)).Available.ShouldBe(0);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_releases_the_stock()
    {
        var product = await _shop.ProductAsync(stock: 3);
        using var customer = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(customer, product.Id, 2);

        (await customer.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).EnsureSuccessStatusCode();

        (await _shop.StockAsync(product.Id)).Available.ShouldBe(3);
        (await _shop.OrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Cancelled);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Customers_cannot_see_or_cancel_each_others_orders()
    {
        var product = await _shop.ProductAsync(stock: 3);
        using var alice = await _shop.CustomerAsync();
        using var bob = await _shop.CustomerAsync();
        var order = await ShopData.PlaceOrderAsync(alice, product.Id, 1);

        // 404, not 403: Bob must not even learn that the order id exists.
        await (await bob.GetAsync($"/api/v1/orders/{order.Id}")).ShouldBeProblemAsync(HttpStatusCode.NotFound);
        await (await bob.PostAsync($"/api/v1/orders/{order.Id}/cancel", null)).ShouldBeProblemAsync(HttpStatusCode.NotFound);
        await (await bob.PostAsync($"/api/v1/orders/{order.Id}/payments", null)).ShouldBeProblemAsync(HttpStatusCode.NotFound);

        var bobsList = await bob.GetFromJsonAsync<Stockflow.Application.Common.PagedResult<OrderDto>>(
            $"/api/v1/orders?customerId={order.CustomerId}", Json.Options);
        bobsList!.TotalCount.ShouldBe(0); // the customerId filter is ignored for customers
    }

    [Fact]
    public async Task Admin_cannot_adjust_stock_below_what_is_reserved()
    {
        var product = await _shop.ProductAsync(stock: 3);
        using var customer = await _shop.CustomerAsync();
        await ShopData.PlaceOrderAsync(customer, product.Id, 2);

        var response = await _admin.PostAsJsonAsync($"/api/v1/inventory/{product.Id}/adjustments",
            new Stockflow.Application.Inventory.AdjustStockRequest(-2, "inventory count"), Json.Options);

        await response.ShouldBeProblemAsync(HttpStatusCode.UnprocessableEntity);
        await _shop.AssertInventoryConsistentAsync(product.Id);
    }

    [Fact]
    public async Task Customers_cannot_use_admin_endpoints_and_anonymous_can_browse()
    {
        var product = await _shop.ProductAsync(stock: 1);
        using var customer = await _shop.CustomerAsync();

        (await customer.PostAsJsonAsync($"/api/v1/inventory/{product.Id}/receipts",
            new Stockflow.Application.Inventory.ReceiveStockRequest(100, null), Json.Options)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Api.CreateClient().GetAsync($"/api/v1/products/{product.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Api.CreateClient().GetAsync("/api/v1/cart")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
