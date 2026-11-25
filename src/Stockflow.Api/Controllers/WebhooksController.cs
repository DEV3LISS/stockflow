using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stockflow.Application.Payments;
using Stockflow.Infrastructure.Payments;

namespace Stockflow.Api.Controllers;

/// <summary>
/// Called by the payment provider, not by users — so there is no JWT. Trust comes from the HMAC signature instead.
/// Always answers 200 for an authentic event (even a duplicate), otherwise the provider keeps retrying.
/// </summary>
[ApiController]
[Route("webhooks/payments")]
[AllowAnonymous]
public sealed partial class WebhooksController(
    PaymentService payments,
    IOptions<PaymentOptions> options,
    TimeProvider clock,
    ILogger<WebhooksController> logger) : ControllerBase
{
    public sealed record WebhookEnvelope(string Id, string Type, WebhookData Data);

    public sealed record WebhookData(string PaymentId);

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // The signature covers the exact raw bytes, so we must read the body ourselves (no model binding first).
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var tolerance = TimeSpan.FromSeconds(options.Value.SignatureToleranceSeconds);
        if (!WebhookSignature.Verify(options.Value.WebhookSecret, Request.Headers[WebhookSignature.HeaderName], body, clock.GetUtcNow(), tolerance))
        {
            LogRejected(logger);
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid signature");
        }

        WebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WebhookEnvelope>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Id) || string.IsNullOrWhiteSpace(envelope.Data?.PaymentId))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed webhook");
        }

        var outcome = await payments.HandleWebhookAsync(new PaymentWebhook(envelope.Id, envelope.Type, envelope.Data.PaymentId), ct);
        return Ok(new { received = true, outcome = outcome.ToString() });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected payment webhook with an invalid signature")]
    private static partial void LogRejected(ILogger logger);
}
