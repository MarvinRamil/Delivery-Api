using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Integration.Application;

/// <summary>
/// Signs webhook payloads in the same style as the PayMongo scheme already
/// used in this codebase: X-Bee-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt; where
/// v1 = HMACSHA256(secret, "{t}.{rawBody}").
/// </summary>
public static class BackofficeWebhookSigner
{
    public const string SignatureHeader = "X-Bee-Signature";
    public const string EventIdHeader = "X-Bee-Event-Id";
    public const string EventTypeHeader = "X-Bee-Event-Type";

    public static string Sign(string secret, long unixTimestamp, string rawBody)
    {
        var payload = $"{unixTimestamp}.{rawBody}";
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));
        return $"t={unixTimestamp},v1={Convert.ToHexStringLower(hash)}";
    }
}
