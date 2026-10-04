using System.Text.RegularExpressions;

namespace BeeLogistics.Api.Observability;

/// <summary>
/// Data Privacy Act (RA 10173) scrubbing for Sentry events.
///
/// Sentry's ScopeExtensions.Populate copies EVERY request header except Cookie onto the event even
/// when SendDefaultPii is false, plus the raw query string and Env["SERVER_NAME"]. On this API that
/// means bearer JWTs, X-Api-Key, Xendit/PayMongo/Didit webhook signatures and X-Forwarded-For would
/// all be shipped off-box. Headers are therefore ALLOWLISTED, not blocklisted — a new header added
/// anywhere in the codebase is dropped by default rather than leaked by default.
/// </summary>
internal static class SentryScrubbing
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Encoding", "Accept-Language",
        "Content-Type", "Content-Length", "Host", "User-Agent",
        "X-Correlation-Id", "traceparent", "tracestate",
    };

    /// <summary>Infrastructure endpoints that must produce neither issues nor transactions.</summary>
    private static readonly string[] IgnoredPathPrefixes =
    {
        "/health/live", "/health/ready", "/metrics", "/swagger", "/scalar",
    };

    /// <summary>
    /// Consumed by SentryOptions.IgnoreTransactions, which drops transactions by name before
    /// BeforeSendTransaction runs. Transaction names are of the form "GET /health/live".
    /// Events need the separate path check in <see cref="BeforeSend"/> — Sentry has no built-in
    /// path filter for events.
    /// </summary>
    internal static readonly StringOrRegex[] IgnoredTransactionPatterns =
    {
        new(new Regex(@"^\S+\s+/health/(live|ready)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        new(new Regex(@"^\S+\s+/metrics\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        new(new Regex(@"^\S+\s+/swagger", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        new(new Regex(@"^\S+\s+/scalar", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    // +639171234567 / 09171234567, and e-mail addresses that leak through exception text.
    private static readonly Regex PhMobile = new(@"(?<!\d)(?:\+?63|0)9\d{9}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);

    internal static SentryEvent? BeforeSend(SentryEvent e, SentryHint hint)
    {
        if (IsIgnoredPath(e.Request.Url))
        {
            return null;
        }

        var req = e.Request;
        foreach (var key in req.Headers.Keys.ToArray())
        {
            if (!AllowedHeaders.Contains(key))
            {
                req.Headers.Remove(key); // Authorization, X-Api-Key, x-signature, X-Forwarded-For, ...
            }
        }

        req.Cookies = null;
        req.QueryString = null; // ?token=, ?email=, ?phone= all live here
        req.Data = null;        // belt and braces over MaxRequestBodySize = None
        req.Env.Remove("REMOTE_ADDR");     // client IP
        req.Env.Remove("SERVER_NAME");     // container hostname
        req.Env.Remove("SERVER_SOFTWARE");
        req.Env.Remove("DOCUMENT_ROOT");   // absolute content-root path of the host/container

        // The opaque User.Id (a Guid) is kept: useful for correlation, not identifying on its own.
        e.User.Email = null;
        e.User.Username = null;
        e.User.IpAddress = null;

        e.ServerName = "bee-backend";

        // Best-effort redaction of free text. Deterministic scrubbing above covers the structured
        // fields; this only catches PII that a log template or exception message spelled out.
        if (e.Message is { } message)
        {
            message.Message = Redact(message.Message);
            message.Formatted = Redact(message.Formatted);
        }

        if (e.SentryExceptions is { } exceptions)
        {
            foreach (var exception in exceptions)
            {
                exception.Value = Redact(exception.Value);
            }
        }

        foreach (var (key, value) in e.Extra.ToArray())
        {
            if (value is string s)
            {
                e.SetExtra(key, Redact(s));
            }
        }

        return e;
    }

    /// <summary>
    /// Breadcrumb.Message is init-only, so a breadcrumb carrying PII is dropped rather than rewritten.
    /// </summary>
    internal static Breadcrumb? BeforeBreadcrumb(Breadcrumb crumb)
        => crumb.Message is { } msg && (PhMobile.IsMatch(msg) || Email.IsMatch(msg)) ? null : crumb;

    private static bool IsIgnoredPath(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return IgnoredPathPrefixes.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return Email.Replace(PhMobile.Replace(input, "[redacted-phone]"), "[redacted-email]");
    }
}
