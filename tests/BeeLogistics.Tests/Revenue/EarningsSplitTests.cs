using BeeLogistics.Modules.Revenue.Domain;
using Xunit;

namespace BeeLogistics.Tests.Revenue;

/// <summary>
/// The split arithmetic behind GitLab #28.
///
/// The bug was two independent formulas for the driver's share: the commission record used
/// <c>gross - round(gross * rate)</c> and the wallet consumers used <c>round(gross * (1 - rate))</c>.
/// At gross 100.10 / 5% those give 95.09 and 95.10 respectively, so the wallet credit, the
/// commission row and the accounting double-entry could not all agree.
/// </summary>
public class EarningsSplitTests
{
    private const decimal Rate = 0.05m;

    /// <summary>
    /// The property the accounting ledger depends on. 100.10 and 33.33 are the fares where the
    /// two old formulas actually diverged; 0.01 is the smallest non-zero amount.
    /// </summary>
    [Theory]
    [InlineData(100.10)]
    [InlineData(33.33)]
    [InlineData(0.01)]
    [InlineData(0.10)]
    [InlineData(500.00)]
    [InlineData(1234.57)]
    [InlineData(99.99)]
    [InlineData(0)]
    public void Commission_plus_driver_always_equals_gross(decimal gross)
    {
        var split = EarningsSplit.For(gross, Rate);

        Assert.Equal(gross, split.CommissionAmount + split.DriverAmount);
    }

    /// <summary>
    /// Pins the exact case from the issue, so a future "simplification" back to independent
    /// rounding fails here with a recognisable number rather than somewhere in the ledger.
    /// </summary>
    [Fact]
    public void The_reported_case_splits_as_documented()
    {
        var split = EarningsSplit.For(100.10m, Rate);

        Assert.Equal(5.01m, split.CommissionAmount);
        Assert.Equal(95.09m, split.DriverAmount);
    }

    /// <summary>
    /// The old independent formula produced 95.10 here. If this ever passes again, the
    /// regression is back.
    /// </summary>
    [Fact]
    public void Driver_share_is_not_the_independently_rounded_value()
    {
        var split = EarningsSplit.For(100.10m, Rate);
        var independentlyRounded = decimal.Round(100.10m * (1m - Rate), 2, MidpointRounding.AwayFromZero);

        Assert.Equal(95.10m, independentlyRounded); // what the old code computed
        Assert.NotEqual(independentlyRounded, split.DriverAmount);
    }

    [Fact]
    public void Commission_is_rounded_to_centavos()
    {
        var split = EarningsSplit.For(100.10m, Rate);

        Assert.Equal(split.CommissionAmount, decimal.Round(split.CommissionAmount, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Boundary_rates_are_allowed(decimal rate)
    {
        var split = EarningsSplit.For(100m, rate);

        Assert.Equal(100m, split.CommissionAmount + split.DriverAmount);
    }

    [Fact]
    public void Zero_rate_gives_the_driver_everything()
    {
        var split = EarningsSplit.For(100m, 0m);

        Assert.Equal(0m, split.CommissionAmount);
        Assert.Equal(100m, split.DriverAmount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Rates_outside_zero_to_one_are_rejected(decimal rate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => EarningsSplit.For(100m, rate));

    /// <summary>
    /// PlatformCommission must not keep its own copy of the arithmetic - it is the record the
    /// ledger is built from, so it has to agree with what the wallet consumers credit.
    /// </summary>
    [Theory]
    [InlineData(100.10)]
    [InlineData(33.33)]
    [InlineData(0.01)]
    public void PlatformCommission_agrees_with_the_split(decimal gross)
    {
        var split = EarningsSplit.For(gross, Rate);
        var commission = new PlatformCommission(Guid.NewGuid(), Guid.NewGuid(), "Cashless", gross, Rate);

        Assert.Equal(split.CommissionAmount, commission.CommissionAmount);
        Assert.Equal(split.DriverAmount, commission.DriverAmount);
        Assert.Equal(gross, commission.CommissionAmount + commission.DriverAmount);
    }
}
