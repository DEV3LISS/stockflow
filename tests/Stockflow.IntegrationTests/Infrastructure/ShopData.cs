using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockflow.Application.Auth;
using Stockflow.Application.Carts;
using Stockflow.Application.Catalog;
using Stockflow.Application.Inventory;
using Stockflow.Application.Orders;
using Stockflow.Application.Payments;
using Stockflow.Domain.Orders;
using Stockflow.Infrastructure.Payments;
using Stockflow.Infrastructure.Persistence;

namespace Stockflow.IntegrationTests.Infrastructure;

/// <summary>Builds shop scenarios through the public API, like real clients would.</summary>
public sealed class ShopData(ApiFactory api, HttpClient admin)
{
    private static int _counter;

    private static int Next() => Interlocked.Increment(ref _counter);

    public async Task<ProductDto> ProductAsync(int stock, decimal price = 1000m)
    {
        var n = Next();
        var category = await PostAsync<CategoryDto>(admin, "/api/v1/categories", new SaveCategoryRequest($"Category {n}", $"cat-{n}"));
        var product = await PostAsync<ProductDto>(admin, "/api/v1/products",
            new SaveProductRequest($"SKU-{n:D5}", $"Product {n}", null, price, category.Id));
        if (stock > 0)
        {
            await PostAsync<StockDto>(admin, $"/api/v1/inventory/{product.Id}/receipts", new ReceiveStockRequest(stock, "TEST"));
        }

        return product;
    }

    /// <summary>A freshly registered customer with an authenticated client.</summary>
    public async Task<HttpClient> CustomerAsync()
    {
        var n = Next();
        using var anonymous = api.CreateClient();
        var auth = await PostAsync<AuthResponse>(anonymous, "/api/v1/auth/register",
            new RegisterRequest($"customer{n}@test.local", $"Customer {n}", "Customer-Password-1"));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    public static async Task AddToCartAsync(HttpClient customer, Guid productId, int quantity)
    {
        var response = await customer.PutAsJsonAsync("/api/v1/cart/items", new SetCartItemRequest(productId, quantity), Json.Options);
        response.EnsureSuccessStatusCode();
    }

    public static Task<HttpResponseMessage> CheckoutAsync(HttpClient customer, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return customer.SendAsync(request);
    }

    public static async Task<OrderDto> PlaceOrderAsync(HttpClient customer, Guid productId, int quantity)
    {
        await AddToCartAsync(customer, productId, quantity);
        var response = await CheckoutAsync(customer);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderDto>(Json.Options))!;
    }

    public static async Task<StartPaymentResult> StartPaymentAsync(HttpClient customer, Guid orderId) =>
        await PostAsync<StartPaymentResult>(customer, $"/api/v1/orders/{orderId}/payments", new { });

    /// <summary>Sends a webhook signed like the provider does (or with a broken signature, for attack tests).</summary>
    public Task<HttpResponseMessage> WebhookAsync(string providerPaymentId, string type, string? eventId = null, string? secret = null,
        DateTimeOffset? signedAt = null)
    {
        var body = JsonSerializer.Serialize(new { id = eventId ?? "evt_" + Guid.NewGuid().ToString("N"), type, data = new { paymentId = providerPaymentId } });
        var timestamp = (signedAt ?? api.Clock.GetUtcNow()).ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Sign(secret ?? ApiFactory.WebhookSecret, timestamp, body));
        return api.CreateClient().SendAsync(request);
    }

    public async Task<StockDto> StockAsync(Guid productId) =>
        (await admin.GetFromJsonAsync<StockDto>($"/api/v1/inventory/{productId}", Json.Options))!;

    public async Task<OrderDto> OrderAsync(Guid orderId) =>
        (await admin.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{orderId}", Json.Options))!;

    /// <summary>
    /// The two invariants of the warehouse, checked straight in the database:
    /// on_hand = sum of the ledger, and reserved = what unpaid orders hold.
    /// </summary>
    public async Task AssertInventoryConsistentAsync(Guid productId)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stock = await db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == productId);
        var ledger = await db.InventoryMovements.Where(m => m.ProductId == productId).SumAsync(m => (int?)m.Quantity) ?? 0;
        var held = await db.Orders.Where(o => o.Status == OrderStatus.PendingPayment)
            .SelectMany(o => o.Lines).Where(l => l.ProductId == productId).SumAsync(l => (int?)l.Quantity) ?? 0;

        stock.OnHand.ShouldBe(ledger, "on_hand must equal the sum of the inventory ledger");
        stock.Reserved.ShouldBe(held, "reserved must equal what PendingPayment orders hold");
        stock.Reserved.ShouldBeLessThanOrEqualTo(stock.OnHand);
    }

    public async Task<int> ExpireDueAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExpiryService>().ExpireDueAsync(CancellationToken.None);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Json.Options);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {url} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json.Options))!;
    }
}
