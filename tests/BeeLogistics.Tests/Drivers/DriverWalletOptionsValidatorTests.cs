using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Shared.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Startup validation for the commission rates.
///
/// These are fractions — 0.05 is 5% — and the natural thing to type into Vault for "five percent"
/// is <c>5</c>. Vault is the highest-precedence configuration source, so that typo overrides every
/// default. <c>EarningsSplit</c> already refuses a rate outside 0..1, but only at the point of use:
/// the driver-offers endpoint and the earnings-credit consumer. Without this validator the app boots
/// clean and the mistake surfaces as every driver's offer list returning 500 and completed bookings
/// never crediting — a config typo wearing the costume of an outage.
///
/// This happened once for real, which is why the tests below name the specific value.
/// </summary>
public class DriverWalletOptionsValidatorTests
{
    private readonly DriverWalletOptionsValidator _validator = new();

    [Fact]
    public void The_shipped_default_is_valid()
    {
        var result = _validator.Validate(null, new DriverWalletOptions());

        Assert.True(result.Succeeded);
        Assert.Equal(PlatformCommissionDefaults.Rate, new DriverWalletOptions().PlatformCommissionRate);
    }

    [Fact]
    public void Five_percent_written_as_five_is_refused_and_says_why()
    {
        // The exact mistake: "5" meaning 5%, which is 500%.
        var result = _validator.Validate(null, new DriverWalletOptions { PlatformCommissionRate = 5m });

        Assert.False(result.Succeeded);
        Assert.Contains("PlatformCommissionRate", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("0.05", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(100)]
    public void A_rate_outside_the_unit_interval_is_refused(decimal rate)
    {
        Assert.False(_validator.Validate(null, new DriverWalletOptions { PlatformCommissionRate = rate }).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(1)]
    public void A_rate_inside_the_unit_interval_is_accepted(decimal rate)
    {
        Assert.True(_validator.Validate(null, new DriverWalletOptions { PlatformCommissionRate = rate }).Succeeded);
    }

    [Fact]
    public void The_cash_override_is_validated_too_when_set()
    {
        // Same trap, second key — and this one governs what a driver is debited for a cash job.
        var result = _validator.Validate(
            null, new DriverWalletOptions { CashDeliveryPlatformChargeRateOverride = 20m });

        Assert.False(result.Succeeded);
        Assert.Contains("CashDeliveryPlatformChargeRateOverride", result.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unset_cash_override_is_valid_and_follows_the_commission_rate()
    {
        var options = new DriverWalletOptions { PlatformCommissionRate = 0.05m };

        Assert.True(_validator.Validate(null, options).Succeeded);
        Assert.Null(options.CashDeliveryPlatformChargeRateOverride);
        Assert.Equal(0.05m, options.CashDeliveryPlatformChargeRate);
    }
}
