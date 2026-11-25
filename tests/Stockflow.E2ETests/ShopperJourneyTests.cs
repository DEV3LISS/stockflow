using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Stockflow.E2ETests;

/// <summary>Runs only when STOCKFLOW_E2E_URL points at a running deployment (e.g. http://localhost:8081).</summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STOCKFLOW_E2E_URL")))
        {
            Skip = "Set STOCKFLOW_E2E_URL to run end-to-end tests against a deployment.";
        }
    }
}

/// <summary>
/// A shopper's whole journey through the deployed container's public API, using raw JSON only (no server DTOs):
/// register → browse → cart → checkout (+ retry) → pay via the provider simulator (webhook delivered twice) → paid.
/// Plus the admin side: the sale appears in the inventory ledger.
/// </summary>
public class ShopperJourneyTests
{
    private static readonly string BaseUrl = Environment.GetEnvironmentVariable("STOCKFLOW_E2E_URL") ?? "";
    private static readonly string AdminEmail = Environment.GetEnvironmentVariable("STOCKFLOW_E2E_ADMIN_EMAIL") ?? "admin@stockflow.local";
    private static readonly string AdminPassword = Environment.GetEnvironmentVariable("STOCKFLOW_E2E_ADMIN_PASSWORD") ?? "ChangeMe-2026!";

    [E2EFact]
    public async Task Shopper_buys_and_pays_and_admin_sees_the_sale()
    {
        using var admin = await LoginAsync(AdminEmail, AdminPassword);
        var run = Guid.NewGuid().ToString("N")[..8];

        // Admin prepares a product with 3 units.
        var category = await Json(await admin.PostAsJsonAsync("/api/v1/categories", new { name = $"E2E {run}", slug = $"e2e-{run}" }), HttpStatusCode.Created);
        var product = await Json(await admin.PostAsJsonAsync("/api/v1/products",
            new { sku = $"E2E-{run}", name = $"E2E product {run}", price = 1990, categoryId = category.GetProperty("id").GetString() }), HttpStatusCode.Created);
        var productId = product.GetProperty("id").GetString();
        await Json(await admin.PostAsJsonAsync($"/api/v1/inventory/{productId}/receipts", new { quantity = 3, reference = "E2E" }));

        // A shopper registers, finds it anonymously in the catalog, puts 2 in the cart.
        using var shopper = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var registered = await Json(await shopper.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"shopper-{run}@e2e.local", fullName = "E2E Shopper", password = "E2E-shopper-pass-1" }), HttpStatusCode.Created);
        registered.GetProperty("user").GetProperty("role").GetString().ShouldBe("Customer");
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.GetProperty("accessToken").GetString());

        var found = await Json(await shopper.GetAsync($"/api/v1/products?search=E2E-{run}"));
        found.GetProperty("items")[0].GetProperty("available").GetInt32().ShouldBe(3);
        await Json(await shopper.PutAsJsonAsync("/api/v1/cart/items", new { productId, quantity = 2 }));

        // Checkout, then the "network glitch" retry with the same key → the same order.
        var key = Guid.NewGuid().ToString();
        var order = await Json(await Checkout(shopper, key), HttpStatusCode.Created);
        var retry = await Json(await Checkout(shopper, key));
        retry.GetProperty("id").GetString().ShouldBe(order.GetProperty("id").GetString());
        order.GetProperty("total").GetDecimal().ShouldBe(3980m);
        var orderId = order.GetProperty("id").GetString();

        // Pay at the simulated provider; it delivers the signed webhook twice.
        var payment = await Json(await shopper.PostAsync($"/api/v1/orders/{orderId}/payments", null), HttpStatusCode.Created);
        var delivered = await Json(await shopper.PostAsync(
            $"/simulator/payments/{payment.GetProperty("providerPaymentId").GetString()}/succeed?deliveries=2", null));
        var outcomes = delivered.GetProperty("deliveries").EnumerateArray().Select(d => d.GetString() ?? "").ToList();
        outcomes[0].ShouldContain("Applied");
        outcomes[1].ShouldContain("Duplicate");

        (await Json(await shopper.GetAsync($"/api/v1/orders/{orderId}"))).GetProperty("status").GetString().ShouldBe("Paid");

        // Warehouse: 1 left, ledger shows the sale.
        var stock = await Json(await admin.GetAsync($"/api/v1/inventory/{productId}"));
        stock.GetProperty("onHand").GetInt32().ShouldBe(1);
        stock.GetProperty("reserved").GetInt32().ShouldBe(0);
        var ledger = await Json(await admin.GetAsync($"/api/v1/inventory/{productId}/movements"));
        ledger.GetProperty("items")[0].GetProperty("type").GetString().ShouldBe("Sale");
    }

    [E2EFact]
    public async Task Deployment_is_healthy_and_observable()
    {
        using var api = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        (await api.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await api.GetStringAsync("/metrics")).ShouldContain("http_server_request_duration_seconds");
        (await api.GetAsync("/swagger/index.html")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var login = await Json(await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> Checkout(HttpClient client, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders");
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(expected, body);
        return JsonElement.Parse(body);
    }
}
