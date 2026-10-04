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
/// Reconciling child-wallet withdrawals whose webhook never arrived (issue #91).
///
/// <para>Without this a PESONet withdrawal — in flight for up to a banking day — sticks in
/// <c>Approved</c> forever, with the driver's mirror still showing money PayMongo has already
/// sent.</para>
/// </summary>
public class ChildWalletWithdrawalReconciliationTests
{
    // Relative to the real clock, not a hardcoded calendar date: WithdrawalRequest.RequestedAt is
    // stamped with the real DateTime.UtcNow at construction time inside each test, and this Now
    // feeds the 20-minute reconciliation cutoff (ReconcileAsync = Now - 20min). A fixed past-dated
    // "Now" works only until the real clock catches up to it - which is exactly what happened here:
    // the withdrawal's real timestamp landed after the hardcoded cutoff and stopped looking "old
    // enough" to reconcile, deterministically once the real date crossed 2026-09-01 ~11:40 UTC.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow.AddHours(1);

    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();

    private ChildWalletWithdrawalReconciliationService Service(bool enabled = true) => new(
        _repo, _accounts,
        Options.Create(new DriverWalletOptions
        {
            PayMongoWithdrawalsEnabled = enabled,
            ChildWithdrawalReconcileAfterMinutes = 20
        }),
        new FixedClock(Now),
        NullLogger<ChildWalletWithdrawalReconciliationService>.Instance);

    private (DriverWallet Wallet, WithdrawalRequest Withdrawal) Stuck(
        decimal mirror = 500m, decimal amount = 100m, bool migrated = true, decimal fee = 0m)
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.AddEarning(mirror);
        if (migrated)
        {
            wallet.LinkPayMongoAccount("org_a");
            wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        }
        _repo.Wallets.Add(wallet);

        var w = new WithdrawalRequest(wallet.DriverId, wallet.Id, amount,
            "1234567890", "GCash", "Juan Dela Cruz", autoApprove: false);
        w.SetProviderDisbursement("paymongo", "tr_out");
        w.AutoApprove();
        if (fee > 0m) w.RecordProviderFee(fee);
        _repo.WithdrawalRequests.Add(w);
        return (wallet, w);
    }

    private void TransferReports(string status, string? error = null) =>
        _accounts.GetChildTransferAsync("org_a", "tr_out", Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_out", status, 100m, 10m, error));

    [Fact]
    public async Task A_succeeded_transfer_is_settled_and_the_mirror_follows_it_down()
    {
        var (wallet, withdrawal) = Stuck();
        TransferReports("succeeded");

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Completed, withdrawal.Status);
        Assert.Equal(400m, wallet.Balance);
    }

    [Fact]
    public async Task A_failed_transfer_is_recorded_without_touching_the_balance()
    {
        // Nothing was deducted locally when the request was made, so there is nothing to return.
        var (wallet, withdrawal) = Stuck();
        TransferReports("failed", "validation failed for transfer_validator.source_account_balance: insufficient");

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Failed, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);
    }

    [Fact]
    public async Task A_still_pending_transfer_is_left_alone()
    {
        // PESONet legitimately takes a banking day. Failing it early would tell the driver their
        // money bounced while it is still on its way.
        var (wallet, withdrawal) = Stuck();
        TransferReports("pending");

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);
    }

    [Fact]
    public async Task An_unknown_transfer_is_left_for_manual_review_not_failed()
    {
        // We cannot tell a transfer that never registered from one we are asking about wrongly.
        // Marking it failed would tell the driver their money is back when it may not be.
        var (wallet, withdrawal) = Stuck();
        _accounts.GetChildTransferAsync("org_a", "tr_out", Arg.Any<CancellationToken>())
            .Returns((PayMongoInternalTransfer?)null);

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);
    }

    [Fact]
    public async Task Platform_path_withdrawals_are_not_touched()
    {
        // Those hold a PendingPayout this service never took, and are the existing
        // WithdrawalReconciliationService's job. Settling one here would skip releasing it.
        var (wallet, withdrawal) = Stuck(migrated: false);

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        await _accounts.DidNotReceiveWithAnyArgs().GetChildTransferAsync(default!, default!, default);
    }

    [Fact]
    public async Task Disabled_flag_polls_nothing()
    {
        Stuck();

        await Service(enabled: false).ReconcileAsync();

        await _accounts.DidNotReceiveWithAnyArgs().GetChildTransferAsync(default!, default!, default);
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_batch()
    {
        var (walletA, a) = Stuck();
        var (walletB, b) = Stuck();
        _accounts.GetChildTransferAsync("org_a", "tr_out", Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("boom"),
                     _ => new PayMongoInternalTransfer("tr_out", "succeeded", 100m, 10m, null));

        await Service().ReconcileAsync();

        Assert.Equal(WithdrawalStatus.Approved, a.Status);
        Assert.Equal(WithdrawalStatus.Completed, b.Status);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// PayMongo charges the fee to the <b>source</b> wallet, so the driver loses amount + fee.
    /// Debiting only the amount leaves the mirror permanently high by the fee — and the balance
    /// reconciler cannot repair that, since it only ever credits; it would report the gap as a
    /// shortfall on every run instead.
    /// </summary>
    [Fact]
    public async Task A_settled_withdrawal_debits_the_fee_as_well_as_the_amount()
    {
        // The live case: PHP 70 wallet, PHP 50 out, PHP 10 fee, PHP 10 left at PayMongo.
        var (wallet, _) = Stuck(mirror: 70m, amount: 50m, fee: 10m);
        TransferReports("succeeded");

        await Service().ReconcileAsync();

        Assert.Equal(10m, wallet.Balance);
    }

    [Fact]
    public async Task A_zero_fee_debits_the_amount_alone()
    {
        var (wallet, _) = Stuck(mirror: 500m, amount: 100m, fee: 0m);
        TransferReports("succeeded");

        await Service().ReconcileAsync();

        Assert.Equal(400m, wallet.Balance);
    }
}
