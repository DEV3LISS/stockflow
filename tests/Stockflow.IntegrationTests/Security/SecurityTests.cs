using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Stockflow.Application.Auth;
using Stockflow.Application.Catalog;
using Stockflow.Application.Common;
using Stockflow.IntegrationTests.Infrastructure;

namespace Stockflow.IntegrationTests.Security;

/// <summary>Attacks from the OWASP API Security Top 10, tried for real against the running API.</summary>
public class SecurityTests(ApiFactory api) : ApiTest(api)
{
    [Fact]
    public async Task Responses_carry_defensive_headers()
    {
        using var client = Api.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "stockflow",
            Audience = "stockflow-clients",
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = "Admin" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("attacker-key-attacker-key-attacker-key-123")), SecurityAlgorithms.HmacSha256),
        });
        using var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forged);

        (await client.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unsigned_alg_none_token_is_rejected()
    {
        // Classic JWT attack: header {"alg":"none"} and no signature.
        static string B64(string s) => Base64UrlEncoder.Encode(s);
        var token = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"sub":"{{Guid.NewGuid()}}","role":"Admin","iss":"stockflow","aud":"stockflow-clients","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""")}.";
        using var client = Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sql_injection_in_search_is_just_text()
    {
        using var client = Api.CreateClient();

        var response = await client.GetAsync("/api/v1/products?search=" + Uri.EscapeDataString("x' OR '1'='1"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PagedResult<ProductDto>>(Json.Options))!.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Oversized_body_is_refused()
    {
        using var client = Api.CreateClient();
        var huge = new LoginRequest("a@b.c", new string('x', 2 * 1024 * 1024));

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", huge);

        await response.ShouldBeProblemAsync(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Registration_cannot_choose_the_admin_role()
    {
        // Mass assignment: an extra "role" field in the JSON must be ignored — sign-up always creates a Customer.
        using var client = Api.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/register", new StringContent(
            """{"email":"sneaky@test.local","fullName":"Sneaky","password":"Sneaky-Password-1","role":"Admin"}""",
            Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json.Options))!;
        auth.User.Role.ShouldBe(Stockflow.Domain.Users.UserRole.Customer);
    }
}
