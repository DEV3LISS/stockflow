using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stockflow.Application.Payments;
using Stockflow.Infrastructure.Payments;

namespace Stockflow.Api.Controllers;

/// <summary>
/// DEMO ONLY — plays the role of the payment provider's page: "the customer paid" / "the card was declined".
/// It sends a signed webhook over real HTTP to /webhooks/payments, exactly like a provider would.
/// <c>deliveries</c> &gt; 1 re-sends the same event to demonstrate idempotent processing.
/// Disabled unless PaymentSimulator:Enabled is true.
/// </summary>
[ApiController]
[Route("simulator/payments")]
[AllowAnonymous]
public sealed class PaymentSimulatorController(
    IOptions<PaymentOptions> options,
    IConfiguration configuration,
    IHttpClientFactory httpClients,
    TimeProvider clock) : ControllerBase
{
    [HttpPost("{providerPaymentId}/{outcome:regex(^(succeed|fail)$)}")]
    public async Task<IActionResult> Complete(string providerPaymentId, string outcome, [FromQuery] int deliveries = 1, CancellationToken ct = default)
    {
        if (!configuration.GetValue("PaymentSimulator:Enabled", false))
        {
            return NotFound();
        }

        var body = JsonSerializer.Serialize(new
        {
            id = "evt_" + Guid.CreateVersion7().ToString("N"),
            type = outcome == "succeed" ? PaymentService.SucceededEvent : PaymentService.FailedEvent,
            data = new { paymentId = providerPaymentId },
        });
        var signature = WebhookSignature.Sign(options.Value.WebhookSecret, clock.GetUtcNow().ToUnixTimeSeconds(), body);

        using var client = httpClients.CreateClient();
        var results = new List<string>();
        for (var i = 0; i < Math.Clamp(deliveries, 1, 5); i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.PublicBaseUrl.TrimEnd('/')}/webhooks/payments")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(WebhookSignature.HeaderName, signature);
            using var response = await client.SendAsync(request, ct);
            results.Add($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }

        return Ok(new { sent = body, deliveries = results });
    }
}
