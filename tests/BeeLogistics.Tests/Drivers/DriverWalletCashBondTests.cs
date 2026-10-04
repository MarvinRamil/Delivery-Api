using BeeLogistics.Modules.Drivers.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// DriverWallet's cashbond field (issue #103): a one-time paid/held/refunded amount, not a running
/// balance — <see cref="DriverWallet.CashBondBalance"/> is either 0 or the fixed amount.
/// </summary>
public class DriverWalletCashBondTests
{
    [Fact]
    public void A_new_wallet_has_not_paid_its_cashbond()
    {
        var wallet = new DriverWallet(Guid.NewGuid());

        Assert.False(wallet.HasPaidCashBond);
        Assert.Equal(0m, wallet.CashBondBalance);
    }

    [Fact]
    public void Marking_the_cashbond_paid_sets_the_balance()
    {
        var wallet = new DriverWallet(Guid.NewGuid());

        wallet.MarkCashBondPaid(1000m);

        Assert.True(wallet.HasPaidCashBond);
        Assert.Equal(1000m, wallet.CashBondBalance);
    }

    [Fact]
    public void Paying_twice_is_refused_at_the_domain_level()
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.MarkCashBondPaid(1000m);

        Assert.Throws<InvalidOperationException>(() => wallet.MarkCashBondPaid(1000m));
    }

    [Fact]
    public void Refunding_clears_the_balance_and_returns_the_amount_released()
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.MarkCashBondPaid(1000m);

        var released = wallet.ClearCashBondOnRefund();

        Assert.Equal(1000m, released);
        Assert.False(wallet.HasPaidCashBond);
        Assert.Equal(0m, wallet.CashBondBalance);
    }

    [Fact]
    public void Refunding_an_unpaid_cashbond_is_refused()
    {
        var wallet = new DriverWallet(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => wallet.ClearCashBondOnRefund());
    }
}
