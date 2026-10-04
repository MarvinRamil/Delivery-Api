using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

namespace BeeLogistics.Modules.Identity.Application.Auth;

/// <summary>
/// Stateless auth helpers: pure functions of their arguments, no injected services and no
/// controller state.
/// <para>
/// Lives in the Application layer because the handlers need it. It started life in
/// Presentation/Controllers, which forced four handlers to import Presentation - an inverted
/// dependency - and pushed <c>ResetPasswordCommandHandler</c> into keeping its own private
/// copies of the password constants. Both are resolved by it living here.
/// </para>
/// </summary>
public static class AuthHelpers
{
    /// <summary>RFC 5321 maximum email length.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Upper bound on a normalized phone number, matching the DB column budget.</summary>
    public const int MaxPhoneLength = 20;

    // Password length rules. The matching user-facing messages live on AuthMessages, which owns
    // every string the auth endpoints return.
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    // Roles a caller may request for themselves. Anything else must go through the
    // authenticated backoffice flow (create-backoffice-user). Compared case-insensitively
    // because ASP.NET Identity normalizes role names before lookup.
    private static readonly HashSet<string> SelfRegistrableRoles =
        new(StringComparer.OrdinalIgnoreCase) { "Customer", "Driver" };

    /// <summary>
    /// SECURITY: Validate email format and length (OWASP Top 10 - A03:2021 Injection)
    /// </summary>
    public static bool IsValidEmailFormat(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        if (email.Length > MaxEmailLength) return false;

        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email.Trim().ToLowerInvariant();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalize a phone number for storage/lookup and external providers.
    /// For PH numbers, converts leading 0 to country code 63 (e.g. 0917... -> 63917...).
    /// Removes non-digit characters.
    /// </summary>
    public static string NormalizePhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return string.Empty;

        var digitsOnly = new string(phoneNumber.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(digitsOnly))
            return string.Empty;

        if (digitsOnly.StartsWith("0", StringComparison.Ordinal))
        {
            // Local PH format like 09xxxxxxxxx -> 639xxxxxxxxx
            digitsOnly = "63" + digitsOnly[1..];
        }

        // If already starts with country code (e.g. 63...), keep as-is.
        return digitsOnly;
    }

    /// <summary>
    /// The length check applied to an already-normalized phone number. Extracted verbatim from
    /// the repeated <c>IsNullOrEmpty || Length &lt; 10 || Length &gt; MAX_PHONE_LENGTH</c> guard.
    /// </summary>
    public static bool IsPlausiblePhoneNumber(string? normalizedPhone)
        => !string.IsNullOrEmpty(normalizedPhone)
           && normalizedPhone.Length >= 10
           && normalizedPhone.Length <= MaxPhoneLength;

    public static bool IsSelfRegistrableRole(string? requestedRole)
        => string.IsNullOrWhiteSpace(requestedRole) || SelfRegistrableRoles.Contains(requestedRole);

    /// <summary>
    /// Hash security answer for storage. Normalizes (trim + lowercase) so comparison is
    /// case-insensitive, then SHA256.
    /// </summary>
    public static string HashSecurityAnswer(string answer)
    {
        var normalized = answer.Trim().ToLowerInvariant();

        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// SECURITY: Get request ID for correlation (OWASP Top 10 - A09:2021 Security Logging).
    /// Stores the generated id on the HttpContext so repeated calls within one request agree.
    /// </summary>
    public static string GetRequestId(HttpContext? httpContext)
    {
        if (httpContext?.Items.TryGetValue("RequestId", out var requestId) == true && requestId is string id)
        {
            return id;
        }

        var newRequestId = Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        if (httpContext != null)
        {
            httpContext.Items["RequestId"] = newRequestId;
        }
        return newRequestId;
    }

    /// <summary>
    /// SECURITY: Get user agent from request (OWASP Top 10 - A09:2021 Security Logging)
    /// </summary>
    public static string? GetUserAgent(HttpContext? httpContext)
        => httpContext?.Request.Headers["User-Agent"].FirstOrDefault();
}
