using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Reconciling driver mirrors against PayMongo (issue #96).
/// <para>
/// This job exists because transaction events on a child account never reach the parent's webhook,
/// so it is the only thing that notices a BeeWallet top-up. That makes it the one place allowed to
/// create money in the mirror from a provider reading — and the invariant is that it may only ever
/// credit, and only when nothing else is in flight.
/// </para>
/// </summary>
public class PayMongoBalanceReconciliationTests
{
    private const string AccountId = "org_03d4711d3f65a6189b624d79";

    private static DriverWallet Activated(decimal mirror)
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        if (mirror > 0) wallet.AddEarning(mirror);
        wallet.LinkPayMongoAccount(AccountId);
        wallet.SetPayMongoWallet($"wallet_{Guid.NewGuid():N}", "817797809438", "ledger_1");

        // Entity.Id stays Guid.Empty until a row is written, and WalletId is what keeps one
        // driver's reconciliation key from colliding with another's in the unique index.
        typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(BeeLogistics.Shared.Abstractions.Entity.Id))!
            .SetValue(wallet, Guid.NewGuid());
        return wallet;
    }

    private static (PayMongoBalanceReconciliationService Service, FakeDriverWalletRepository Repo, IPayMongoAccountsClient Accounts)
        Create(decimal? remoteBalance, bool enabled = true)
    {
        var repo = new FakeDriverWalletRepository();
        var accounts = Substitute.For<IPayMongoAccountsClient>();

        accounts.GetWalletAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(remoteBalance is null
                ? (PayMongoChildWallet?)null
                : new PayMongoChildWallet("wallet_x", AccountId, "active", "default",
                    "817797809438", "DRIVER ONE", "ledger_1", remoteBalance, 0m));

        var service = new PayMongoBalanceReconciliationService(
            repo, accounts,
            Options.Create(new DriverWalletOptions
            {
                PayMongoBalanceReconciliationEnabled = enabled,
                PayMongoEarningsPushEnabled = true,
            }),
            TimeProvider.System,
            NullLogger<PayMongoBalanceReconciliationService>.Instance);

        return (service, repo, accounts);
    }

    [Fact]
    public async Task Money_PayMongo_holds_that_the_mirror_missed_is_credited()
    {
        // Exactly the reported failure: ₱1180 locally, ₱1230 at PayMongo after a QR top-up whose
        // event was never delivered.
        var (service, repo, _) = Create(remoteBalance: 1230m);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();

        Assert.Equal(1230m, wallet.Balance);

        var tx = Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.TopUp);
        Assert.Equal(50m, tx.Amount);
        Assert.Equal(WalletBucket.Personal, tx.Bucket);
        Assert.Equal(WalletTransactionStatus.Completed, tx.Status);
    }

    /// <summary>
    /// The direction that must never auto-correct. Money left PayMongo without us recording it;
    /// debiting on a reading we cannot explain risks taking it from the driver twice.
    /// </summary>
    [Fact]
    public async Task A_balance_below_the_mirror_is_never_debited()
    {
        var (service, repo, _) = Create(remoteBalance: 900m);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();

        Assert.Equal(1180m, wallet.Balance);
        Assert.Empty(repo.Transactions);
    }

    /// <summary>
    /// A withdrawal debits Balance into PendingPayout *before* PayMongo sends, so the wallet reads
    /// low locally and high remotely. Crediting that gap would hand the driver money already on its
    /// way out of the wallet.
    /// </summary>
    [Fact]
    public async Task A_wallet_mid_withdrawal_is_skipped_entirely()
    {
        var (service, repo, _) = Create(remoteBalance: 1180m);
        var wallet = Activated(1180m);
        wallet.RequestWithdrawal(500m);   // Balance 680, PendingPayout 500, PayMongo still 1180
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();

        Assert.Equal(680m, wallet.Balance);
        Assert.Equal(500m, wallet.PendingPayout);
        Assert.Empty(repo.Transactions);
    }

    /// <summary>
    /// Same reasoning for a claim-before-send row: written before the provider call, so the views
    /// disagree until it resolves on its own.
    /// </summary>
    [Fact]
    public async Task A_wallet_with_a_pending_transaction_is_skipped_entirely()
    {
        var (service, repo, _) = Create(remoteBalance: 1230m);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);
        repo.Transactions.Add(new WalletTransaction(
            wallet.Id, WalletTransactionType.Earning, WalletBucket.Personal, 50m,
            WalletTransactionStatus.Pending, "in flight"));

        await service.ReconcileAsync();

        Assert.Equal(1180m, wallet.Balance);
        Assert.DoesNotContain(repo.Transactions, t => t.Type == WalletTransactionType.TopUp);
    }

    [Fact]
    public async Task Running_twice_over_the_same_drift_credits_once()
    {
        var (service, repo, _) = Create(remoteBalance: 1230m);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();
        // Second run sees a mirror that now matches, so there is nothing left to do.
        await service.ReconcileAsync();

        Assert.Equal(1230m, wallet.Balance);
        Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.TopUp);
    }

    [Fact]
    public async Task An_unreadable_balance_leaves_the_mirror_alone()
    {
        var (service, repo, _) = Create(remoteBalance: null);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();

        Assert.Equal(1180m, wallet.Balance);
        Assert.Empty(repo.Transactions);
    }

    /// <summary>
    /// A disabled sweep must announce itself. Returning silently makes "switched off" and "broken"
    /// produce identical evidence — no logs, and a Hangfire run reporting success — which is exactly
    /// how a flag that never took effect was first read as a job that never ran.
    /// </summary>
    [Fact]
    public async Task Being_disabled_is_logged_rather_than_returning_silently()
    {
        var repo = new FakeDriverWalletRepository();
        repo.Wallets.Add(Activated(1180m));
        var logger = new CapturingLogger<PayMongoBalanceReconciliationService>();

        var service = new PayMongoBalanceReconciliationService(
            repo, Substitute.For<IPayMongoAccountsClient>(),
            Options.Create(new DriverWalletOptions
            {
                PayMongoBalanceReconciliationEnabled = false,
                PayMongoEarningsPushEnabled = true,
            }),
            TimeProvider.System, logger);

        await service.ReconcileAsync();

        var line = Assert.Single(logger.Messages);
        Assert.Contains("[PAYMONGO] [RECONCILE]", line);
        // Names the exact setting, so the log answers "why" without a code read.
        Assert.Contains("PayMongoBalanceReconciliationEnabled", line);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_flag_being_off_reads_nothing_from_PayMongo()
    {
        var (service, repo, accounts) = Create(remoteBalance: 1230m, enabled: false);
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);

        await service.ReconcileAsync();

        Assert.Equal(1180m, wallet.Balance);
        await accounts.DidNotReceive().GetWalletAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A wallet can sit in <c>Activated</c> with no account number: the activation webhook marks
    /// the status, then the follow-up fetch of the wallet's account details fails. The paging query
    /// filters on status alone, so such a wallet <i>is</i> handed to this job — and calling PayMongo
    /// for it would mean reconciling against a wallet we cannot actually address.
    /// </summary>
    [Fact]
    public async Task An_activated_wallet_with_no_account_number_is_left_alone()
    {
        var (service, repo, accounts) = Create(remoteBalance: 1230m);
        var half = new DriverWallet(Guid.NewGuid());
        half.AddEarning(1180m);
        half.LinkPayMongoAccount(AccountId);
        half.SetPayMongoActivationStatus(PayMongoActivationStatus.Verified);
        half.SetPayMongoActivationStatus(PayMongoActivationStatus.Activated);
        // SetPayMongoWallet deliberately never called - the fetch that supplies it failed.
        repo.Wallets.Add(half);

        await service.ReconcileAsync();

        Assert.Equal(1180m, half.Balance);
        Assert.Empty(repo.Transactions);
        await accounts.DidNotReceive().GetWalletAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unmigrated wallet never even reaches this job - the paging query filters on activation
    /// status. Asserted so a later widening of that query cannot quietly start feeding local-balance
    /// drivers into a PayMongo reconciliation.
    /// </summary>
    /// <summary>
    /// The case that reached a live wallet: with the earnings push off, Balance holds locally
    /// credited earnings that were never sent to PayMongo, while the child wallet holds only QR
    /// top-ups. Comparing them reported a PHP 1,160 shortfall on a wallet where nothing was wrong.
    /// </summary>
    [Fact]
    public async Task Before_the_earnings_push_is_enabled_nothing_is_compared()
    {
        var repo = new FakeDriverWalletRepository();
        var wallet = Activated(1180m);
        repo.Wallets.Add(wallet);
        var accounts = Substitute.For<IPayMongoAccountsClient>();
        accounts.GetWalletAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoChildWallet("wallet_x", AccountId, "active", "default",
                "817797809438", "DRIVER", "ledger_1", 20m, 0m));

        var service = new PayMongoBalanceReconciliationService(
            repo, accounts,
            Options.Create(new DriverWalletOptions
            {
                PayMongoBalanceReconciliationEnabled = true,
                PayMongoEarningsPushEnabled = false,   // phase 1: the mirror is not a mirror
            }),
            TimeProvider.System,
            NullLogger<PayMongoBalanceReconciliationService>.Instance);

        await service.ReconcileAsync();

        Assert.Equal(1180m, wallet.Balance);
        Assert.Empty(repo.Transactions);
        // Not even read: there is nothing meaningful to compare against.
        await accounts.DidNotReceive().GetWalletAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unmigrated_wallet_is_never_paged_in()
    {
        var (service, repo, accounts) = Create(remoteBalance: 1230m);
        var plain = new DriverWallet(Guid.NewGuid());
        plain.AddEarning(1180m);
        repo.Wallets.Add(plain);

        await service.ReconcileAsync();

        Assert.Equal(1180m, plain.Balance);
        Assert.Empty(repo.Transactions);
        await accounts.DidNotReceive().GetWalletAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>One bad wallet must not strand every wallet ordered after it.</summary>
    [Fact]
    public async Task A_failure_on_one_wallet_does_not_stop_the_sweep()
    {
        var repo = new FakeDriverWalletRepository();
        var first = Activated(1180m);
        var second = Activated(1180m);
        // Order the fake's keyset page deterministically.
        if (first.Id.CompareTo(second.Id) > 0) (first, second) = (second, first);
        repo.Wallets.Add(first);
        repo.Wallets.Add(second);

        var accounts = Substitute.For<IPayMongoAccountsClient>();
        accounts.GetWalletAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("PayMongo is having a day"),
                     _ => new PayMongoChildWallet("wallet_x", AccountId, "active", "default",
                            "817797809438", "DRIVER", "ledger_1", 1230m, 0m));

        var service = new PayMongoBalanceReconciliationService(
            repo, accounts,
            Options.Create(new DriverWalletOptions
            {
                PayMongoBalanceReconciliationEnabled = true,
                PayMongoEarningsPushEnabled = true,
            }),
            TimeProvider.System,
            NullLogger<PayMongoBalanceReconciliationService>.Instance);

        await service.ReconcileAsync();

        Assert.Equal(1180m, first.Balance);   // the one that threw
        Assert.Equal(1230m, second.Balance);  // still reconciled
    }
}
