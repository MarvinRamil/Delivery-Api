using System.Text.RegularExpressions;

namespace BeeLogistics.Shared.Infrastructure.Security;

/// <summary>
/// Utility for masking sensitive PII (Personally Identifiable Information) in strings,
/// primarily for use in application logs to ensure DPA compliance.
/// </summary>
public static class PiiMasker
{
    private static readonly Regex EmailRegex = new Regex(@"[^@\s]+@[^@\s]+\.[^@\s]+", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new Regex(@"(\+?63|0)?9\d{9}", RegexOptions.Compiled);

    /// <summary>
    /// Masks email addresses in a string (e.g., j***n@example.com).
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        
        var parts = email.Split('@');
        if (parts.Length != 2) return "***";

        var name = parts[0];
        var domain = parts[1];

        if (name.Length <= 2) return name.Substring(0, 1) + "***@" + domain;
        
        return name.Substring(0, 1) + "***" + name.Substring(name.Length - 1) + "@" + domain;
    }

    /// <summary>
    /// Masks phone numbers in a string (e.g., 0917****123).
    /// Uses digit extraction so short or malformed input never causes IndexOutOfRange.
    /// </summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 7) return "***";

        return digits.Substring(0, 4) + "****" + digits.Substring(digits.Length - 3);
    }

    /// <summary>
    /// Masks a full name or display name (e.g., "John Doe" -> "J***").
    /// For use in audit logs and DPA-sensitive output.
    /// </summary>
    public static string MaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var t = name.Trim();
        return t.Length > 0 ? t[0] + "***" : "***";
    }

    /// <summary>
    /// Masks all detected PII in a block of text.
    /// </summary>
    public static string MaskAll(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var result = EmailRegex.Replace(input, m => MaskEmail(m.Value));
        result = PhoneRegex.Replace(result, m => MaskPhone(m.Value));

        return result;
    }
}
