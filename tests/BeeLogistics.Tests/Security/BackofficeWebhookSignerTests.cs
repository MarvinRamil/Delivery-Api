using System.Security.Cryptography;
using System.Text;
using BeeLogistics.Modules.Integration.Application;
using Xunit;

namespace BeeLogistics.Tests.Security;

/// <summary>
/// Webhook signature format shared with the back-office backend receiver:
/// X-Bee-Signature: t=&lt;unix&gt;,v1=HMACSHA256(secret, "{t}.{body}").
/// </summary>
public class BackofficeWebhookSignerTests
{
    [Fact]
    public void Signature_has_timestamp_and_v1_hmac_of_timestamped_body()
    {
        const string secret = "shared-secret";
        const string body = """{"eventId":"abc","type":"driver_application.submitted"}""";
        const long timestamp = 1_751_600_000;

        var signature = BackofficeWebhookSigner.Sign(secret, timestamp, body);

        var expectedHash = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
        Assert.Equal($"t={timestamp},v1={expectedHash}", signature);
    }

    [Fact]
    public void Different_body_or_secret_or_timestamp_changes_the_signature()
    {
        const string body = "{}";
        var baseline = BackofficeWebhookSigner.Sign("secret", 1000, body);

        Assert.NotEqual(baseline, BackofficeWebhookSigner.Sign("secret", 1000, "{\"a\":1}"));
        Assert.NotEqual(baseline, BackofficeWebhookSigner.Sign("other", 1000, body));
        Assert.NotEqual(baseline, BackofficeWebhookSigner.Sign("secret", 1001, body));
    }
}
