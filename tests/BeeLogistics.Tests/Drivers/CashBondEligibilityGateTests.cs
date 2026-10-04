using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The cashbond gate (issue #103) rides on the same choke point every booking-dispatch path
/// already funnels through — <see cref="GetDriversEligibleForCashJobQueryHandler"/> — rather than
/// adding a second cross-module check. A driver who has not paid is excluded here even when
/// otherwise commission-eligible.
/// </summary>
public class CashBondEligibilityGateTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private static readonly Guid DriverId = Guid.NewGuid();

    private GetDriversEligibleForCashJobQueryHandler Handler() =>
        new(_repo, Options.Create(new DriverWalletOptions()));

    /// <summary>Otherwise fully eligible: on the PayMongo path, funded, nothing owed.</summary>
    private DriverWallet EligibleWallet()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "817797809438", "ledger_1");
        wallet.AddBeeWalletTopUp(1000m);
        _repo.Wallets.Add(wallet);
        return wallet;
    }

    [Fact]
    public async Task An_unpaid_cashbond_excludes_an_otherwise_eligible_driver()
    {
        EligibleWallet(); // HasPaidCashBond is false by default

        var result = await Handler().Handle(
            new GetDriversEligibleForCashJobQuery(new[] { DriverId }, 100m), CancellationToken.None);

        Assert.DoesNotContain(DriverId, result);
    }

    [Fact]
    public async Task A_paid_cashbond_allows_an_otherwise_eligible_driver_through()
    {
        var wallet = EligibleWallet();
        wallet.MarkCashBondPaid(1000m);

        var result = await Handler().Handle(
            new GetDriversEligibleForCashJobQuery(new[] { DriverId }, 100m), CancellationToken.None);

        Assert.Contains(DriverId, result);
    }
}
