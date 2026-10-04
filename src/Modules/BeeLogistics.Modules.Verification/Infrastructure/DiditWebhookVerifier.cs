using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Verification.Infrastructure;

/// <summary>
/// Verifies Didit webhook authenticity: X-Signature is HMAC-SHA256(secret, rawBody) hex,
/// X-Timestamp is unix seconds and must be within the replay tolerance window.
/// </summary>
public static class DiditWebhookVerifier
{
    public const string SignatureHeader = "X-Signature";
    public const string TimestampHeader = "X-Timestamp";

    /// <summary>Reports which specific check failed, for diagnostics. rawBody must be the exact bytes
    /// received on the wire - never round-trip through a string first (e.g. a StreamReader can silently
    /// strip a UTF-8 BOM, which would make a byte-for-byte-correct signature look like a mismatch).</summary>
    public static bool Verify(byte[] rawBody, string? signatureHex, string? timestamp, string secret, int toleranceSeconds, out string? failureReason)
    {
        failureReason = null;

        if (string.IsNullOrWhiteSpace(signatureHex)) { failureReason = "missing X-Signature header"; return false; }
        if (string.IsNullOrWhiteSpace(timestamp)) { failureReason = "missing X-Timestamp header"; return false; }
        if (string.IsNullOrWhiteSpace(secret)) { failureReason = "WebhookSecret not configured"; return false; }

        if (!long.TryParse(timestamp, out var ts)) { failureReason = $"X-Timestamp '{timestamp}' is not a valid unix timestamp"; return false; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skewSeconds = now - ts;
        if (Math.Abs(skewSeconds) > toleranceSeconds)
        {
            failureReason = $"timestamp outside tolerance (skew={skewSeconds}s, tolerance={toleranceSeconds}s) - check server clock or WebhookToleranceSeconds";
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHex.Trim());
        }
        catch (FormatException)
        {
            failureReason = "X-Signature is not valid hex";
            return false;
        }

        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        if (!CryptographicOperations.FixedTimeEquals(computed, provided))
        {
            failureReason = "HMAC signature mismatch - WebhookSecret likely doesn't match the Didit dashboard's signing secret for this webhook endpoint";
            return false;
        }

        return true;
    }
}
