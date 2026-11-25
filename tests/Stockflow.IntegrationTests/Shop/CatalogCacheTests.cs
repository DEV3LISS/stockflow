using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockflow.Application.Catalog;
using Stockflow.Application.Common;
using Stockflow.Infrastructure.Persistence;
using Stockflow.IntegrationTests.Infrastructure;

namespace Stockflow.IntegrationTests.Shop;

public class CatalogCacheTests(ApiFactory api) : ApiTest(api)
{
    private async Task SetPriceBehindTheAppsBackAsync(Guid productId, decimal price)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlAsync($"UPDATE products SET price = {price} WHERE id = {productId}");
    }

    [Fact]
    public async Task Product_page_is_served_from_cache_and_invalidated_on_change()
    {
        using var admin = await Api.CreateAdminClientAsync();
        var shop = new ShopData(Api, admin);
        var product = await shop.ProductAsync(stock: 1, price: 1000m);
        using var visitor = Api.CreateClient();
        (await visitor.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", Json.Options))!.Price.ShouldBe(1000m);

        // A change made directly in SQL is invisible while the entry lives: proof the page came from the cache.
        await SetPriceBehindTheAppsBackAsync(product.Id, 1.00m);
        (await visitor.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", Json.Options))!.Price.ShouldBe(1000m);

        // A change through the API invalidates by tag — visible immediately.
        (await admin.PutAsJsonAsync($"/api/v1/products/{product.Id}",
            new SaveProductRequest(product.Sku, product.Name, null, 1200m, product.CategoryId), Json.Options)).EnsureSuccessStatusCode();
        (await visitor.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", Json.Options))!.Price.ShouldBe(1200m);
    }

    [Fact]
    public async Task Category_page_is_invalidated_when_a_product_is_deactivated()
    {
        using var admin = await Api.CreateAdminClientAsync();
        var product = await new ShopData(Api, admin).ProductAsync(stock: 1);
        var url = $"/api/v1/products?category={product.CategorySlug}";
        using var visitor = Api.CreateClient();
        (await visitor.GetFromJsonAsync<PagedResult<ProductDto>>(url, Json.Options))!.TotalCount.ShouldBe(1);

        (await admin.PostAsync($"/api/v1/products/{product.Id}/deactivate", null)).EnsureSuccessStatusCode();

        (await visitor.GetFromJsonAsync<PagedResult<ProductDto>>(url, Json.Options))!.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stale_cached_availability_can_never_oversell()
    {
        using var admin = await Api.CreateAdminClientAsync();
        var shop = new ShopData(Api, admin);
        var product = await shop.ProductAsync(stock: 1);
        using var alice = await shop.CustomerAsync();
        using var bob = await shop.CustomerAsync();
        (await bob.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", Json.Options))!.Available.ShouldBe(1); // cached "1"

        await ShopData.PlaceOrderAsync(alice, product.Id, 1);
        (await bob.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", Json.Options))!.Available.ShouldBe(1); // still says 1…

        await ShopData.AddToCartAsync(bob, product.Id, 1);
        await (await ShopData.CheckoutAsync(bob)).ShouldBeProblemAsync(HttpStatusCode.Conflict); // …but checkout asks the database
    }
}

/// <summary>Redis configured but unreachable: the shop must keep working on the database alone.</summary>
public class RedisDownTests(ApiFactory api) : ApiTest(api)
{
    [Fact]
    public async Task Catalog_keeps_working_when_redis_is_down()
    {
        using var admin = await Api.CreateAdminClientAsync();
        var product = await new ShopData(Api, admin).ProductAsync(stock: 3);
        using var noRedis = Api.WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500"));
        using var visitor = noRedis.CreateClient();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            var response = await visitor.GetAsync($"/api/v1/products/{product.Id}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}
