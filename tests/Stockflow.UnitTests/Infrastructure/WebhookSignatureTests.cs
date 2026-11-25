using Stockflow.Infrastructure.Payments;

namespace Stockflow.UnitTests.Infrastructure;

public class WebhookSignatureTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789abcdef";
    private const string Body = """{"id":"evt_1","type":"payment.succeeded","data":{"paymentId":"pay_1"}}""";
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static string SignNow() => WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);

    [Fact]
    public void Valid_signature_is_accepted() =>
        WebhookSignature.Verify(Secret, SignNow(), Body, Now, Tolerance).ShouldBeTrue();

    [Fact]
    public void Tampered_body_is_rejected() =>
        WebhookSignature.Verify(Secret, SignNow(), Body.Replace("pay_1", "pay_2", StringComparison.Ordinal), Now, Tolerance).ShouldBeFalse();

    [Fact]
    public void Wrong_secret_is_rejected() =>
        WebhookSignature.Verify("another-secret-another-secret-another-1", SignNow(), Body, Now, Tolerance).ShouldBeFalse();

    [Fact]
    public void Replayed_old_request_is_rejected() =>
        WebhookSignature.Verify(Secret, SignNow(), Body, Now.AddMinutes(6), Tolerance).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("v1=deadbeef")]
    public void Malformed_headers_are_rejected(string? header) =>
        WebhookSignature.Verify(Secret, header, Body, Now, Tolerance).ShouldBeFalse();
}
