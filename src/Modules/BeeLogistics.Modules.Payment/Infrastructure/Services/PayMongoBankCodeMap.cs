using BeeLogistics.Modules.Payment.Application.Banks;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// Resolves the bank code stored on a withdrawal record to the BIC PayMongo's Transfers
/// API wants as <c>destination_account.bic</c>.
///
/// The institutions themselves live in <see cref="PhBankCatalog"/> (generated from
/// PayMongo's receiving-institutions list), which also owns the legacy-alias handling.
/// All that is left here is the last provider-specific step: catalog entry -> BIC.
/// The database always stores the friendly code; normalization happens at call time so
/// records created under one gateway remain usable under the other.
///
/// Every key in <see cref="XenditBankCodeMap"/> must resolve here (enforced by
/// BankCodeMapParityTests).
/// </summary>
public static class PayMongoBankCodeMap
{
    /// <summary>Friendly keys this map understands (used by tests to assert parity across providers).</summary>
    public static IReadOnlyCollection<string> KnownCodes => PhBankCatalog.KnownCodes;

    /// <summary>
    /// Returns the PayMongo bank identifier for a friendly code. Codes that already
    /// look like a BIC (8 or 11 chars, no spaces, all letters/digits) pass through.
    /// Throws for unknown codes: unlike a heuristic prefix, a wrong BIC would send
    /// money to the wrong institution.
    /// </summary>
    /// <param name="rail">
    /// Rail the transfer will take. A handful of institutions are listed once per rail
    /// under different BICs (PNB), so the rail has to be known before the code is chosen;
    /// the default matches the InstaPay-first behaviour of the transfer payload.
    /// </param>
    public static string Normalize(string bankCode, TransferRail rail = TransferRail.Instapay)
    {
        if (string.IsNullOrWhiteSpace(bankCode))
            throw new ArgumentException("Bank code is required for PayMongo transfers", nameof(bankCode));

        var normalized = bankCode.Trim();

        // Catalog codes, legacy aliases and Xendit-style PH_ channel codes all resolve here.
        if (TryResolve(normalized, rail, out var bic))
            return bic;

        // Already a BIC-style identifier
        if ((normalized.Length == 8 || normalized.Length == 11) && normalized.All(char.IsLetterOrDigit))
            return normalized.ToUpperInvariant();

        throw new ArgumentException($"Unknown bank code '{bankCode}' for PayMongo transfers", nameof(bankCode));
    }

    /// <summary>
    /// True when the code names an institution we can actually pay out to today.
    /// Callers use this to reject a withdrawal before touching the wallet, instead of
    /// letting <see cref="Normalize"/> throw from inside the gateway.
    /// </summary>
    public static bool IsPayable(string? bankCode) => PhBankCatalog.TryResolve(bankCode, out _);

    private static bool TryResolve(string code, TransferRail rail, out string bic)
    {
        if (PhBankCatalog.TryResolve(code, out var bank) && bank.BicFor(rail) is { } railBic)
        {
            bic = railBic;
            return true;
        }

        bic = "";
        return false;
    }
}
