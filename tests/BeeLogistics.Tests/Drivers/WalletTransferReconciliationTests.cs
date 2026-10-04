using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Settling wallet transfers PayMongo left unfinished (issue #95).
/// <para>
/// A parent↔child sweep can return <c>"pending"</c> — measured on live with a ₱50 Personal → TopUp.
/// The handler leaves the buckets untouched, so without this job the legs stay Pending forever and,
/// because a Pending leg makes the balance reconciler skip the wallet, the driver drops out of every
/// automated correction.
/// </para>
/// </summary>
public class WalletTransferReconciliationTests
{
    private const string AccountId = "org_03d4711d3f65a6189b624d79";
    private const string TransferId = "tr_6ca2c46195c28842be957925";

    private static DriverWallet Wallet(decimal beePay, decimal cash)
    {
        var w = new DriverWallet(Guid.NewGuid());
        if (beePay > 0) w.AddEarning(beePay);
        if (cash > 0) w.AddTopUp(cash);
        w.LinkPayMongoAccount(AccountId);
        w.SetPayMongoWallet("wallet_x", "817797809438", "ledger_1");
        typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(BeeLogistics.Shared.Abstractions.Entity.Id))!
            .SetValue(w, Guid.NewGuid());
        return w;
    }

    /// <summary>The pair the handler leaves behind on a pending sweep.</summary>
    private static (WalletTransaction Out, WalletTransaction In) PendingLegs(
        DriverWallet w, WalletBucket from, WalletBucket to, decimal amount)
    {
        var o = new WalletTransaction(w.Id, WalletTransactionType.WalletTransferOut, from, amount,
            WalletTransactionStatus.Pending, $"Wallet transfer out ({from} -> {to})");
        var i = new WalletTransaction(w.Id, WalletTransactionType.WalletTransferIn, to, amount,
            WalletTransactionStatus.Pending, $"Wallet transfer in ({from} -> {to})");
        o.SetProviderPaymentId(TransferId);
        i.SetProviderPaymentId(TransferId);
        return (o, i);
    }

    private static (WalletTransferReconciliationService S, FakeDriverWalletRepository R)
        Create(PayMongoInternalTransfer? remote)
    {
        var repo = new FakeDriverWalletRepository();
        var accounts = Substitute.For<IPayMongoAccountsClient>();
        accounts.GetChildTransferAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(remote);
        var service = new WalletTransferReconciliationService(
            repo, accounts,
            Options.Create(new DriverWalletOptions
            {
                PayMongoEarningsPushEnabled = true,
                WalletTransferReconcileAfterSeconds = 0,
            }),
            TimeProvider.System,
            NullLogger<WalletTransferReconciliationService>.Instance);
        return (service, repo);
    }

    [Fact]
    public async Task A_transfer_that_settled_moves_the_buckets_and_completes_both_legs()
    {
        var (s, repo) = Create(new PayMongoInternalTransfer(TransferId, "succeeded", 50m, 0m, null));
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.Personal, WalletBucket.TopUp, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();

        // The handler never moved these; the resolver does, once and only once money has landed.
        Assert.Equal(950m, w.Balance);
        Assert.Equal(550m, w.TopUpBalance);
        Assert.Equal(WalletTransactionStatus.Completed, o.Status);
        Assert.Equal(WalletTransactionStatus.Completed, i.Status);
    }

    [Fact]
    public async Task The_reverse_direction_settles_the_other_way()
    {
        var (s, repo) = Create(new PayMongoInternalTransfer(TransferId, "succeeded", 50m, 0m, null));
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.TopUp, WalletBucket.Personal, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();

        Assert.Equal(1050m, w.Balance);
        Assert.Equal(450m, w.TopUpBalance);
    }

    [Fact]
    public async Task A_transfer_that_failed_moves_nothing_and_fails_both_legs()
    {
        var (s, repo) = Create(new PayMongoInternalTransfer(TransferId, "failed", 50m, 0m, "insufficient"));
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.Personal, WalletBucket.TopUp, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();

        Assert.Equal(1000m, w.Balance);
        Assert.Equal(500m, w.TopUpBalance);
        Assert.Equal(WalletTransactionStatus.Failed, o.Status);
        Assert.Equal(WalletTransactionStatus.Failed, i.Status);
    }

    [Fact]
    public async Task A_transfer_still_pending_is_left_exactly_as_it_was()
    {
        var (s, repo) = Create(new PayMongoInternalTransfer(TransferId, "pending", 50m, 0m, null));
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.Personal, WalletBucket.TopUp, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();

        Assert.Equal(1000m, w.Balance);
        Assert.Equal(WalletTransactionStatus.Pending, o.Status);
    }

    /// <summary>
    /// No answer is not the same as "it did not happen". The money may well have moved, so the
    /// only safe reading is to ask again later.
    /// </summary>
    [Fact]
    public async Task An_unreadable_transfer_is_left_pending_rather_than_written_off()
    {
        var (s, repo) = Create(remote: null);
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.Personal, WalletBucket.TopUp, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();

        Assert.Equal(1000m, w.Balance);
        Assert.Equal(WalletTransactionStatus.Pending, o.Status);
    }

    /// <summary>Settling twice would move the buckets twice for one transfer.</summary>
    [Fact]
    public async Task Running_twice_settles_once()
    {
        var (s, repo) = Create(new PayMongoInternalTransfer(TransferId, "succeeded", 50m, 0m, null));
        var w = Wallet(beePay: 1000m, cash: 500m);
        repo.Wallets.Add(w);
        var (o, i) = PendingLegs(w, WalletBucket.Personal, WalletBucket.TopUp, 50m);
        repo.Transactions.Add(o); repo.Transactions.Add(i);

        await s.ReconcileAsync();
        await s.ReconcileAsync();

        Assert.Equal(950m, w.Balance);
        Assert.Equal(550m, w.TopUpBalance);
    }
}
