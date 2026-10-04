using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Messaging.Infrastructure;

/// <summary>
/// Verifies that an inbound appservice transaction really came from our homeserver.
/// </summary>
/// <remarks>
/// <para>
/// The <c>hs_token</c> is the <b>only</b> thing standing between this endpoint and anyone who
/// finds it. A forged transaction would let an attacker write arbitrary messages into the admin
/// transcript, attributed to whoever they chose — so this is compared in constant time, the same
/// way <c>DiditWebhookVerifier</c> handles its signature.
/// </para>
/// <para>
/// There is no timestamp or replay window here, unlike the Didit webhook: Matrix transactions are
/// idempotent by design (a duplicate event id is a no-op) and Synapse legitimately retries the same
/// transaction until it gets a 2xx, so rejecting replays would break normal delivery.
/// </para>
/// </remarks>
public static class MatrixTransactionAuthenticator
{
    /// <summary>
    /// True when <paramref name="presented"/> matches <paramref name="expected"/>. Constant time in
    /// the length of the tokens, and false whenever either side is missing.
    /// </summary>
    public static bool IsAuthentic(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals returns false for a length mismatch without comparing, which leaks only
        // the length — not the content — and that is the standard accepted trade-off here.
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Pulls the token out of an <c>Authorization: Bearer …</c> header. Synapse has sent the token
    /// this way since Matrix v1.4; the older <c>?access_token=</c> query form is deliberately not
    /// accepted, since honouring it would put a credential in every access log.
    /// </summary>
    public static string? ExtractBearer(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;

        const string prefix = "Bearer ";
        return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }
}
