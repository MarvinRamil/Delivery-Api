namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// Maps the friendly bank names/codes stored on withdrawal records (BPI, BDO,
/// GCASH, ...) to Xendit Philippines payout channel codes (PH_BPI, ...).
/// The database always stores the friendly code; normalization happens at call
/// time so records created under one gateway remain usable under the other.
/// </summary>
public static class XenditBankCodeMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BPI"] = "PH_BPI",
        ["BDO"] = "PH_BDO",
        ["UBP"] = "PH_UBP",
        ["UNIONBANK"] = "PH_UBP",
        ["GCASH"] = "PH_GCASH",
        ["SEC"] = "PH_SEC",
        ["SECURITY"] = "PH_SEC",
        ["SECURITY BANK"] = "PH_SEC",
        ["MAYA"] = "PH_MAYA",
        ["LANDBANK"] = "PH_LANDBANK",
        ["METROBANK"] = "PH_METROBANK",
        ["MBTC"] = "PH_METROBANK",
        ["PNB"] = "PH_PNB",
        ["RCBC"] = "PH_RCBC",
        ["CHINABANK"] = "PH_CHINABANK",
        ["EASTWEST"] = "PH_EASTWEST",
    };

    /// <summary>Friendly keys this map understands (used by tests to assert parity across providers).</summary>
    public static IReadOnlyCollection<string> KnownCodes => Map.Keys;

    /// <summary>If already in PH_XXX form, returns as-is; unknown short codes get a PH_ prefix heuristic.</summary>
    public static string Normalize(string bankCode)
    {
        if (string.IsNullOrWhiteSpace(bankCode))
            return "PH_UBP"; // fallback to a common channel

        var normalized = bankCode.Trim();
        if (normalized.StartsWith("PH_", StringComparison.OrdinalIgnoreCase))
            return normalized;

        if (Map.TryGetValue(normalized, out var code))
            return code;

        // Heuristic: if short (e.g. 2–5 chars), prefix with PH_
        if (normalized.Length <= 8 && !normalized.Contains(' '))
            return "PH_" + normalized.ToUpperInvariant();

        return normalized;
    }
}
