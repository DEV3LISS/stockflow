using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Stockflow.Application.Abstractions;

namespace Stockflow.Infrastructure.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Shared secret for webhook signatures (HMAC-SHA256). From an environment variable in real deployments.</summary>
    [Required]
    [MinLength(32)]
    public string WebhookSecret { get; init; } = null!;

    /// <summary>Base URL the simulator's payment links point to.</summary>
    public string PublicBaseUrl { get; init; } = "http://localhost:8080";

    /// <summary>Reject webhooks signed longer ago than this (replay protection).</summary>
    public int SignatureToleranceSeconds { get; init; } = 300;
}

/// <summary>
/// Stand-in for a real provider (Stripe, YooKassa, CloudPayments…). Creates a payment id and a payment page URL;
/// the outcome is triggered through the simulator endpoint, which sends a signed webhook exactly like a provider.
/// </summary>
public sealed class SimulatedPaymentProvider(IOptions<PaymentOptions> options) : IPaymentProvider
{
    public Task<ProviderPayment> CreatePaymentAsync(Guid orderId, decimal amount, CancellationToken ct)
    {
        var id = "pay_" + Guid.CreateVersion7().ToString("N");
        return Task.FromResult(new ProviderPayment(id, $"{options.Value.PublicBaseUrl.TrimEnd('/')}/simulator/payments/{id}"));
    }
}

/// <summary>
/// Webhook signature in the style used by major providers: header <c>Stockflow-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>,
/// where v1 = HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;").
/// <list type="bullet">
/// <item>Signing the raw body: any change to the payload breaks the signature.</item>
/// <item>Including the timestamp + tolerance: a captured request cannot be replayed later.</item>
/// <item>Constant-time comparison: the time to reject does not leak how many leading characters matched.</item>
/// </list>
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "Stockflow-Signature";

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    public static bool Verify(string secret, string? header, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var parts = header.Split(',', StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);

        if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !parts.TryGetValue("v1", out var received))
        {
            return false;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > tolerance.TotalSeconds)
        {
            return false;
        }

        var expected = Sign(secret, timestamp, body)["t=".Length..].Split(",v1=")[1];
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(received));
    }
}
