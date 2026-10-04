namespace BeeLogistics.Modules.Accounting.Domain;

/// <summary>
/// Ledger account codes for double-entry style audit.
/// Immutable; only add new codes, never remove.
/// </summary>
public static class AccountCode
{
    public const string DriverPersonalWallet = "DRIVER_PERSONAL_WALLET";
    public const string DriverTopUpWallet = "DRIVER_TOPUP_WALLET";
    public const string PendingPayout = "PENDING_PAYOUT";
    public const string XenditOut = "XENDIT_OUT";
    public const string PlatformRevenue = "PLATFORM_REVENUE";
    public const string CustomerPayment = "CUSTOMER_PAYMENT";

    /// <summary>
    /// Real cash received from a driver via a top-up checkout (Xendit/PayMongo) - distinct from
    /// CustomerPayment, which is rider-side money.
    /// </summary>
    public const string DriverTopUpPayment = "DRIVER_TOPUP_PAYMENT";

    /// <summary>
    /// Suspense/contra account for manual admin corrections to a driver's TopUp wallet (cash
    /// disputes). Not a real external account - exists so a manual adjustment still posts a
    /// balanced double-entry pair instead of only mutating DriverTopUpWallet on its own.
    /// </summary>
    public const string ManualAdjustment = "MANUAL_ADJUSTMENT";
}
