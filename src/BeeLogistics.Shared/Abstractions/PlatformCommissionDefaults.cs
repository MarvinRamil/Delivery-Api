namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Where the platform commission rate lives, for the modules that cannot see
/// <c>DriverWalletOptions</c>.
/// </summary>
/// <remarks>
/// The Bookings module quotes the driver their share at offer time, but it cannot reference the
/// Drivers module — Drivers already references Bookings, so the dependency would be circular.
/// Rather than let a second default drift away from the first, both sides take the rate from
/// here: <c>DriverWalletOptions.PlatformCommissionRate</c> uses it as its initial value, and
/// Bookings reads the same configuration key.
/// </remarks>
public static class PlatformCommissionDefaults
{
    /// <summary>The configuration key bound by <c>DriverWalletOptions</c>.</summary>
    public const string RateConfigurationKey = "DriverWallet:PlatformCommissionRate";

    /// <summary>Platform's share of a completed fare, as a fraction. 0.05 = 5%.</summary>
    public const decimal Rate = 0.05m;

    /// <summary>
    /// Optional override for what a cash delivery is settled at. Unset means the commission
    /// rate applies. Anything quoting a cash driver their obligation must honour this, or the
    /// figure shown will not be the figure debited.
    /// </summary>
    public const string CashChargeRateOverrideConfigurationKey =
        "DriverWallet:CashDeliveryPlatformChargeRateOverride";
}
