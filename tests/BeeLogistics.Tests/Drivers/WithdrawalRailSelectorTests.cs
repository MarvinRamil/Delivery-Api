using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Payment.Application.Banks;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Rail selection for withdrawals (issue #91 §4).
///
/// The behaviour these lock in: an amount InstaPay cannot carry is <b>rejected with a reason</b>,
/// never silently downgraded to PESONet. A driver told "instant" who is paid on the next banking
/// day raises a support ticket, and the old <c>PayMongoGateway</c> switch did exactly that.
/// </summary>
public class WithdrawalRailSelectorTests
{
    private static PhBank Bank(bool instapay, bool pesonet, string name = "Test Bank") =>
        new("TEST", name,
            Bic: instapay ? "TESTPHM1" : "TESTPHM2",
            InstapayBic: instapay ? "TESTPHM1" : null,
            PesonetBic: pesonet ? "TESTPHM2" : null,
            Instapay: instapay,
            Pesonet: pesonet,
            Type: "bank");

    [Fact]
    public void Small_amount_on_a_dual_rail_bank_goes_instant()
    {
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(true, true), 500m);

        Assert.Null(rejection);
        Assert.Equal(TransferRail.Instapay, selection!.Rail);
        Assert.Equal("TESTPHM1", selection.Bic);
        Assert.True(selection.IsInstant);
        Assert.Equal("Instant", selection.ArrivalDescription);
    }

    [Fact]
    public void Above_the_instapay_ceiling_uses_pesonet_when_the_bank_supports_it()
    {
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(true, true), 60_000m);

        Assert.Null(rejection);
        Assert.Equal(TransferRail.Pesonet, selection!.Rail);
        // BICs differ per rail - sending the InstaPay BIC over PESONet is how money reaches the
        // wrong institution, which is why BicFor(rail) exists.
        Assert.Equal("TESTPHM2", selection.Bic);
        Assert.False(selection.IsInstant);
        Assert.Equal("Arrives next banking day", selection.ArrivalDescription);
    }

    [Fact]
    public void Pesonet_only_institution_routes_to_pesonet_even_for_a_small_amount()
    {
        // Coverage, not the ceiling, is the real reason to support both rails.
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(false, true), 100m);

        Assert.Null(rejection);
        Assert.Equal(TransferRail.Pesonet, selection!.Rail);
    }

    [Fact]
    public void Instapay_only_institution_over_the_ceiling_is_rejected_not_downgraded()
    {
        // The important one. Silently switching rails would pay the driver on the next banking day
        // after telling them "instant"; and if the bank cannot take PESONet at all, the transfer
        // simply fails later with a far less useful message.
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(true, false, "GCash"), 60_000m);

        Assert.Null(selection);
        Assert.Contains("50,000", rejection!.Reason);
        Assert.Contains("GCash", rejection.Reason);
    }

    [Fact]
    public void Above_every_rail_limit_names_the_cap()
    {
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(true, true), 20_000_000m);

        Assert.Null(selection);
        Assert.Contains("10,000,000", rejection!.Reason);
    }

    [Fact]
    public void Institution_with_no_usable_rail_is_rejected()
    {
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(false, false), 100m);

        Assert.Null(selection);
        Assert.NotNull(rejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_amounts_are_rejected(decimal amount)
    {
        var (selection, rejection) = WithdrawalRailSelector.Select(Bank(true, true), amount);

        Assert.Null(selection);
        Assert.NotNull(rejection);
    }

    [Fact]
    public void Exactly_the_instapay_ceiling_still_goes_instant()
    {
        var (selection, _) = WithdrawalRailSelector.Select(Bank(true, true), PhBank.InstapayLimit);
        Assert.Equal(TransferRail.Instapay, selection!.Rail);
    }
}
