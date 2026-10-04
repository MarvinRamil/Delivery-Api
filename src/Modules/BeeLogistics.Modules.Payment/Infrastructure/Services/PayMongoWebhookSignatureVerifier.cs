using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

public interface IPayMongoWebhookSignatureVerifier
{
    /// <summary>
    /// Verifies a Paymongo-Signature header ("t=&lt;unix&gt;,te=&lt;hex&gt;,li=&lt;hex&gt;")
    /// against the raw request body: HMAC-SHA256 over "{t}.{rawBody}" with the
    /// per-webhook secret, compared in constant time against the live (li) or
    /// test (te) component. Rejects timestamps outside the tolerance window
    /// (replay protection).
    /// </summary>
    bool Verify(string rawBody, string signatureHeader, string webhookSecret, bool liveMode, TimeSpan tolerance, TimeProvider clock);
}

public sealed class PayMongoWebhookSignatureVerifier : IPayMongoWebhookSignatureVerifier
{
    public bool Verify(string rawBody, string signatureHeader, string webhookSecret, bool liveMode, TimeSpan tolerance, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(webhookSecret))
            return false;

        long? timestamp = null;
        string? testSignature = null;
        string? liveSignature = null;

        foreach (var part in signatureHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = part[..separatorIndex];
            var value = part[(separatorIndex + 1)..];

            switch (key)
            {
                case "t" when long.TryParse(value, out var ts):
                    timestamp = ts;
                    break;
                case "te":
                    testSignature = value;
                    break;
                case "li":
                    liveSignature = value;
                    break;
            }
        }

        if (timestamp is null)
            return false;

        // Replay protection: reject signatures outside the tolerance window (both
        // stale and future-dated, allowing for minor clock skew via the same window).
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp.Value) > tolerance.TotalSeconds)
            return false;

        var expectedHex = liveMode ? liveSignature : testSignature;
        if (string.IsNullOrEmpty(expectedHex))
            return false;

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var computed = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(webhookSecret),
            Encoding.UTF8.GetBytes($"{timestamp.Value}.{rawBody}"));

        return CryptographicOperations.FixedTimeEquals(computed, expectedBytes);
    }
}
