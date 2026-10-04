using System.Security.Cryptography;
using System.Text;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class PayMongoWebhookSignatureVerifierTests
{
    private const string Secret = "whsk_test_secret";
    private const string Body = """{"data":{"id":"evt_1","attributes":{"type":"checkout_session.payment.paid","livemode":true}}}""";

    private readonly PayMongoWebhookSignatureVerifier _verifier = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    private string Sign(string body, long timestamp, string secret = Secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    [Fact]
    public void Valid_live_signature_passes()
    {
        var header = $"t={Now},te=,li={Sign(Body, Now)}";
        Assert.True(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Valid_test_signature_passes_in_test_mode()
    {
        var header = $"t={Now},te={Sign(Body, Now)},li=";
        Assert.True(_verifier.Verify(Body, header, Secret, liveMode: false, Tolerance, _clock));
    }

    [Fact]
    public void Live_event_does_not_accept_test_signature_component()
    {
        // signature present only in te, but event is livemode
        var header = $"t={Now},te={Sign(Body, Now)},li=";
        Assert.False(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var header = $"t={Now},te=,li={Sign(Body, Now)}";
        Assert.False(_verifier.Verify(Body.Replace("evt_1", "evt_2"), header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Wrong_secret_fails()
    {
        var header = $"t={Now},te=,li={Sign(Body, Now, "whsk_other")}";
        Assert.False(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Stale_timestamp_outside_tolerance_fails()
    {
        var stale = Now - 301;
        var header = $"t={stale},te=,li={Sign(Body, stale)}";
        Assert.False(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Future_timestamp_outside_tolerance_fails()
    {
        var future = Now + 301;
        var header = $"t={future},te=,li={Sign(Body, future)}";
        Assert.False(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Timestamp_within_tolerance_passes()
    {
        var slightlyOld = Now - 299;
        var header = $"t={slightlyOld},te=,li={Sign(Body, slightlyOld)}";
        Assert.True(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=notanumber,li=abc")]
    [InlineData("li=abcdef")] // missing timestamp
    [InlineData("t=1751371200,li=not-hex!!")]
    public void Malformed_header_fails(string header)
    {
        Assert.False(_verifier.Verify(Body, header, Secret, liveMode: true, Tolerance, _clock));
    }

    [Fact]
    public void Empty_secret_fails()
    {
        var header = $"t={Now},te=,li={Sign(Body, Now)}";
        Assert.False(_verifier.Verify(Body, header, "", liveMode: true, Tolerance, _clock));
    }
}
