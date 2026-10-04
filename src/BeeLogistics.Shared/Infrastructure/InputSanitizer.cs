using System.Text;
using System.Text.RegularExpressions;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Input sanitization utility to prevent XSS and injection attacks
/// SECURITY: Sanitizes user-generated content before storage/display
/// </summary>
public static class InputSanitizer
{
    // SECURITY: Regex timeout to prevent ReDoS attacks (1 second max)
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Allowed HTML tags for rich text (if needed in future)
    private static readonly HashSet<string> AllowedHtmlTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "strong", "em", "u", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6"
    };

    /// <summary>
    /// Sanitizes plain text input - removes HTML tags and encodes special characters
    /// Use this for plain text fields that should not contain HTML
    /// </summary>
    public static string SanitizePlainText(string? input, int maxLength = 10000)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        try
        {
            // Trim whitespace
            var sanitized = input.Trim();

            // Limit length to prevent DoS
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);

            // Remove all HTML tags (basic protection)
            sanitized = Regex.Replace(sanitized, @"<[^>]+>", string.Empty, RegexOptions.None, RegexTimeout);

            // HTML encode special characters to prevent XSS
            sanitized = HtmlEncode(sanitized);

            // Remove control characters (except newlines and tabs)
            sanitized = Regex.Replace(sanitized, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", string.Empty, RegexOptions.None, RegexTimeout);

            return sanitized;
        }
        catch (RegexMatchTimeoutException)
        {
            // If regex times out, return empty string to be safe
            return string.Empty;
        }
    }

    /// <summary>
    /// Sanitizes HTML content - allows safe HTML tags only
    /// Use this for rich text fields that need basic formatting
    /// </summary>
    public static string SanitizeHtml(string? input, int maxLength = 50000)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        try
        {
            var sanitized = input.Trim();

            // Limit length
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength);

            // Remove script tags and event handlers (XSS prevention)
            sanitized = Regex.Replace(sanitized, @"<script[^>]*>.*?</script>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);
            sanitized = Regex.Replace(sanitized, @"javascript:", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
            sanitized = Regex.Replace(sanitized, @"on\w+\s*=", string.Empty, RegexOptions.IgnoreCase, RegexTimeout); // Remove onclick, onload, etc.

            // Remove dangerous attributes
            sanitized = Regex.Replace(sanitized, @"\s*(style|onerror|onload|onclick|onmouseover)\s*=", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);

            // Remove iframe, object, embed tags (can execute scripts)
            sanitized = Regex.Replace(sanitized, @"<(iframe|object|embed)[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

            // Remove control characters
            sanitized = Regex.Replace(sanitized, @"[\x00-\x08\x0B-\x0C\x0E-\x1F]", string.Empty, RegexOptions.None, RegexTimeout);

            return sanitized;
        }
        catch (RegexMatchTimeoutException)
        {
            // If regex times out, return empty string to be safe
            return string.Empty;
        }
    }

    /// <summary>
    /// Sanitizes file names to prevent path traversal
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "file";

        // Remove path components
        var sanitized = Path.GetFileName(fileName);

        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        // Limit length
        if (sanitized.Length > 255)
        {
            var ext = Path.GetExtension(sanitized);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExt.Substring(0, Math.Min(255 - ext.Length, nameWithoutExt.Length)) + ext;
        }

        return sanitized;
    }

    /// <summary>
    /// Sanitizes URLs to prevent javascript: and data: protocol attacks
    /// </summary>
    public static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        try
        {
            var sanitized = url.Trim();

            // Only allow http, https, mailto protocols
            if (!Regex.IsMatch(sanitized, @"^(https?|mailto):", RegexOptions.IgnoreCase, RegexTimeout))
            {
                // If no protocol, assume https
                if (!sanitized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !sanitized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !sanitized.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    sanitized = "https://" + sanitized;
                }
            }

            // Remove javascript: and data: protocols
            sanitized = Regex.Replace(sanitized, @"^(javascript|data|vbscript):", string.Empty, RegexOptions.IgnoreCase, RegexTimeout);

            return sanitized;
        }
        catch (RegexMatchTimeoutException)
        {
            // If regex times out, return empty string to be safe
            return string.Empty;
        }
    }

    /// <summary>
    /// HTML encodes special characters to prevent XSS
    /// </summary>
    private static string HtmlEncode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder(input.Length * 2);
        foreach (var c in input)
        {
            switch (c)
            {
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '\'':
                    sb.Append("&#x27;");
                    break;
                case '&':
                    sb.Append("&amp;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Validates and sanitizes email addresses
    /// </summary>
    public static string SanitizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var sanitized = email.Trim().ToLowerInvariant();

        // Limit length first to prevent DoS
        if (sanitized.Length > 255)
            throw new ArgumentException("Email address too long", nameof(email));

        try
        {
            // Basic email validation
            if (!Regex.IsMatch(sanitized, @"^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$", RegexOptions.IgnoreCase, RegexTimeout))
            {
                throw new ArgumentException("Invalid email format", nameof(email));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ArgumentException("Invalid email format", nameof(email));
        }

        return sanitized;
    }

    /// <summary>
    /// Removes SQL injection patterns (defense in depth - EF Core already protects)
    /// </summary>
    public static string SanitizeSqlInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        try
        {
            // Remove common SQL injection patterns
            var sanitized = input;
            var dangerousPatterns = new[]
            {
                @"(\b(SELECT|INSERT|UPDATE|DELETE|DROP|CREATE|ALTER|EXEC|EXECUTE|UNION|SCRIPT)\b)",
                @"(--|;|\/\*|\*\/|xp_|sp_)",
                @"([';]|--|;|/\*|\*/)"
            };

            foreach (var pattern in dangerousPatterns)
            {
                sanitized = Regex.Replace(sanitized, pattern, string.Empty, RegexOptions.IgnoreCase, RegexTimeout);
            }

            return sanitized;
        }
        catch (RegexMatchTimeoutException)
        {
            // If regex times out, return empty string to be safe
            return string.Empty;
        }
    }
}

