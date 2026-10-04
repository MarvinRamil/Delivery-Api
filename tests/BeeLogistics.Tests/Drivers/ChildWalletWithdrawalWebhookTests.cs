using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Settling a withdrawal that left the driver's own PayMongo wallet (issue #91).
///
/// <para>
/// This path reserved nothing locally, so the reservation machinery the platform path uses is not
/// merely unnecessary here — it is actively wrong. Falling through to it broke three ways: the
/// transaction lookup returns null and the withdrawal sticks in <c>Approved</c> forever;
/// <c>CompleteWithdrawal</c> throws on an empty <c>PendingPayout</c>; and <c>RejectWithdrawal</c>
/// would <b>credit</b> the mirror on failure, inventing money the driver never lost.
/// </para>
/// </summary>
public class ChildWalletWithdrawalWebhookTests
{
    private static readonly Guid DriverId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly FakeDriverWalletRepository _repo = new();

    private (ProcessDisbursementWebhookCommandHandler Handler, DriverWallet Wallet, WithdrawalRequest Withdrawal)
        Setup(decimal mirrorBalance = 500m, decimal amount = 100m, decimal fee = 0m)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.AddEarning(mirrorBalance);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        _repo.Wallets.Add(wallet);

        var withdrawal = new WithdrawalRequest(
            DriverId, wallet.Id, amount, "1234567890", "GCash", "Juan Dela Cruz",
            autoApprove: false);
        withdrawal.SetProviderDisbursement("paymongo", "tr_out");
        withdrawal.AutoApprove();
        if (fee > 0m) withdrawal.RecordProviderFee(fee);
        _repo.WithdrawalRequests.Add(withdrawal);

        var handler = new ProcessDisbursementWebhookCommandHandler(
            _repo,
            Substitute.For<IDriverOutboxPublisher>(),
            NullLogger<ProcessDisbursementWebhookCommandHandler>.Instance);

        return (handler, wallet, withdrawal);
    }

    [Fact]
    public async Task Success_completes_the_withdrawal_and_brings_the_mirror_down()
    {
        // The money genuinely left PayMongo, so the mirror must follow it. Leaving it alone would
        // show the driver a balance they no longer have.
        var (handler, wallet, withdrawal) = Setup(mirrorBalance: 500m, amount: 100m);

        var result = await handler.Handle(new ProcessDisbursementWebhookCommand(
            "paymongo", "tr_out", "transfer.outward.successful", "SUCCEEDED", null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Completed, withdrawal.Status);
        Assert.Equal(400m, wallet.Balance);
        Assert.Equal(0m, wallet.PendingPayout);
    }

    [Fact]
    public async Task Failure_records_it_without_inventing_money()
    {
        // Nothing was deducted locally when the request was made, so there is nothing to give back.
        // The platform path's RejectWithdrawal would have credited 100 here out of thin air.
        var (handler, wallet, withdrawal) = Setup(mirrorBalance: 500m, amount: 100m);

        var result = await handler.Handle(new ProcessDisbursementWebhookCommand(
            "paymongo", "tr_out", "transfer.outward.failed", "FAILED",
            "validation failed for transfer_validator.source_account_balance: insufficient"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Failed, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);
        Assert.Equal(0m, wallet.PendingPayout);
    }

    [Fact]
    public async Task A_non_terminal_status_changes_nothing()
    {
        var (handler, wallet, withdrawal) = Setup();

        var result = await handler.Handle(new ProcessDisbursementWebhookCommand(
            "paymongo", "tr_out", "transfer.outward.pending", "PENDING", null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);
    }

    /// <summary>
    /// The webhook path must debit the same total the reconciler does — the fee comes out of the
    /// driver's own wallet, so amount alone leaves the mirror high by it forever.
    /// </summary>
    [Fact]
    public async Task Success_debits_the_fee_as_well_as_the_amount()
    {
        // The live case: PHP 70 wallet, PHP 50 out, PHP 10 fee, PHP 10 left at PayMongo.
        var (handler, wallet, _) = Setup(mirrorBalance: 70m, amount: 50m, fee: 10m);

        var result = await handler.Handle(new ProcessDisbursementWebhookCommand(
            "paymongo", "tr_out", "transfer.outward.successful", "SUCCEEDED", null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, wallet.Balance);
    }
}
