namespace BeeLogistics.Modules.Revenue.Domain;

/// <summary>
/// The single definition of how a fare is divided between the platform and the driver.
///
/// Exists because the split used to be computed two different ways: the commission record
/// derived the driver's share as <c>gross - round(gross * rate)</c> while the wallet-crediting
/// consumers computed <c>round(gross * (1 - rate))</c> independently. Those disagree by a
/// centavo on ordinary fares — at gross 100.10 and 5%, one says 95.09 and the other 95.10 —
/// so the wallet, the commission row and the accounting ledger could not all be right at once,
/// and the double-entry set did not balance.
///
/// Only the commission is rounded; the driver takes the exact remainder. That makes
/// <c>CommissionAmount + DriverAmount == GrossAmount</c> true by construction for any input,
/// which is the property the ledger depends on.
/// </summary>
public readonly record struct EarningsSplit
{
    private EarningsSplit(decimal grossAmount, decimal commissionRate, decimal commissionAmount, decimal driverAmount)
    {
        GrossAmount = grossAmount;
        CommissionRate = commissionRate;
        CommissionAmount = commissionAmount;
        DriverAmount = driverAmount;
    }

    /// <summary>The full fare being divided. Expected to be a money amount at 2 decimal places.</summary>
    public decimal GrossAmount { get; }

    /// <summary>The platform's share as a fraction, e.g. 0.05 for 5%.</summary>
    public decimal CommissionRate { get; }

    /// <summary>The platform's cut, rounded to centavos.</summary>
    public decimal CommissionAmount { get; }

    /// <summary>The driver's cut: the exact remainder, never independently rounded.</summary>
    public decimal DriverAmount { get; }

    /// <exception cref="ArgumentOutOfRangeException">
    /// If <paramref name="commissionRate"/> is outside 0..1, which would silently invert or
    /// inflate the split.
    /// </exception>
    public static EarningsSplit For(decimal grossAmount, decimal commissionRate)
    {
        if (commissionRate < 0m || commissionRate > 1m)
            throw new ArgumentOutOfRangeException(
                nameof(commissionRate), commissionRate, "Commission rate must be between 0 and 1.");

        var commissionAmount = decimal.Round(grossAmount * commissionRate, 2, MidpointRounding.AwayFromZero);
        return new EarningsSplit(grossAmount, commissionRate, commissionAmount, grossAmount - commissionAmount);
    }
}
