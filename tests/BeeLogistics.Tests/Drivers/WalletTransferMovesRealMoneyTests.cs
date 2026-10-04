using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// BeePay ↔ Cash Wallet transfers for a migrated driver (issue #95).
/// <para>
/// The invariant: for a driver whose balance lives at PayMongo, the local buckets may only move if
/// the money moved too. Moving the numbers alone would let a driver mint cash-job float backed by
/// money in a wallet the platform cannot reach — and then withdraw it.
/// </para>
/// </summary>
public class WalletTransferMovesRealMoneyTests
{
    private const string AccountId = "org_03d4711d3f65a6189b624d79";
    private const string AccountNumber = "817797809438";

    private static DriverWallet Wallet(decimal beePay, decimal cashWallet, bool migrated)
    {
        var w = new DriverWallet(Guid.NewGuid());
        if (beePay > 0) w.AddEarning(beePay);
        if (cashWallet > 0) w.AddTopUp(cashWallet);
        if (migrated)
        {
            w.LinkPayMongoAccount(AccountId);
            w.SetPayMongoWallet($"wallet_{Guid.NewGuid():N}", AccountNumber, "ledger_1");
        }
        typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(BeeLogistics.Shared.Abstractions.Entity.Id))!
            .SetValue(w, Guid.NewGuid());
        return w;
    }

    private static PayMongoInternalTransfer Result(string status) =>
        new($"tr_{Guid.NewGuid():N}", status, 0m, 0m, status == "failed" ? "insufficient" : null);

    private static (TransferWalletBalanceCommandHandler H, FakeDriverWalletRepository R, IPayMongoAccountsClient A)
        Create(string transferStatus = "succeeded", bool pushEnabled = true)
    {
        var repo = new FakeDriverWalletRepository();
        var accounts = Substitute.For<IPayMongoAccountsClient>();
        accounts.SweepFromChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result(transferStatus));
        accounts.TransferToChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result(transferStatus));

        var handler = new TransferWalletBalanceCommandHandler(
            repo,
            Options.Create(new DriverWalletOptions { PayMongoEarningsPushEnabled = pushEnabled }),
            accounts,
            NullLogger<TransferWalletBalanceCommandHandler>.Instance);
        return (handler, repo, accounts);
    }

    [Fact]
    public async Task BeePay_to_Cash_Wallet_pulls_the_money_out_of_the_child_wallet()
    {
        var (h, repo, accounts) = Create();
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(700m, w.Balance);
        Assert.Equal(800m, w.TopUpBalance);

        // The float the platform draws commission from must be backed by money it actually holds.
        await accounts.Received(1).SweepFromChildAsync(
            AccountId, AccountNumber, Arg.Any<string>(), 300m,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.All(repo.Transactions, t => Assert.Equal(WalletTransactionStatus.Completed, t.Status));
    }

    [Fact]
    public async Task Cash_Wallet_to_BeePay_pushes_the_money_into_the_child_wallet()
    {
        var (h, repo, accounts) = Create();
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.TopUp, WalletBucket.Personal, 200m), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1200m, w.Balance);
        Assert.Equal(300m, w.TopUpBalance);

        await accounts.Received(1).TransferToChildAsync(
            AccountId, AccountNumber, Arg.Any<string>(), 200m,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The one that matters: no money moved, so no bucket may move either.</summary>
    [Fact]
    public async Task A_failed_transfer_moves_no_balance_and_leaves_both_legs_failed()
    {
        var (h, repo, _) = Create(transferStatus: "failed");
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, w.Balance);
        Assert.Equal(500m, w.TopUpBalance);
        Assert.All(repo.Transactions, t => Assert.Equal(WalletTransactionStatus.Failed, t.Status));
    }

    /// <summary>
    /// Failed and Succeeded are not complements. An unsettled transfer must neither move the
    /// balances nor be written off — the legs stay Pending, which also holds the balance
    /// reconciler off this wallet until it resolves.
    /// </summary>
    [Fact]
    public async Task An_unsettled_transfer_moves_no_balance_and_leaves_both_legs_pending()
    {
        var (h, repo, _) = Create(transferStatus: "pending");
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, w.Balance);
        Assert.Equal(500m, w.TopUpBalance);
        Assert.NotEmpty(repo.Transactions);
        Assert.All(repo.Transactions, t => Assert.Equal(WalletTransactionStatus.Pending, t.Status));
    }

    [Fact]
    public async Task An_unmigrated_driver_keeps_the_original_bookkeeping_only_path()
    {
        var (h, repo, accounts) = Create();
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: false);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(700m, w.Balance);
        Assert.Equal(800m, w.TopUpBalance);
        await accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }

    /// <summary>
    /// Before phase 2 a migrated driver's BeePay balance is still a local number — earnings were
    /// never pushed — so moving real money would send funds that are not there.
    /// </summary>
    [Fact]
    public async Task A_migrated_driver_before_the_earnings_push_still_moves_only_numbers()
    {
        var (h, repo, accounts) = Create(pushEnabled: false);
        var w = Wallet(beePay: 1000m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(700m, w.Balance);
        await accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task Overdrawing_BeePay_never_reaches_PayMongo()
    {
        var (h, repo, accounts) = Create();
        var w = Wallet(beePay: 100m, cashWallet: 500m, migrated: true);
        repo.Wallets.Add(w);

        var result = await h.Handle(
            new TransferWalletBalanceCommand(w.DriverId, WalletBucket.Personal, WalletBucket.TopUp, 300m), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(100m, w.Balance);
        await accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }
}
