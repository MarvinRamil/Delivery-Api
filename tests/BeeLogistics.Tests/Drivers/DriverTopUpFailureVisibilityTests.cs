using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// GitLab #64: a declined top-up payment left no trace anywhere. PayMongo's payment.failed event
/// was not routed at all, the handler's FAILED branch was unreachable, and a WalletTransaction
/// was only ever written on success - so "my payment was declined" and "still waiting" looked
/// identical in the driver app until the record quietly expired 25 hours later.
///
/// Writing a Failed ledger row is safe: every balance aggregate filters on Completed (or excludes
/// Failed), and top-ups sit in the TopUp bucket the personal-balance calculation never reads.
/// </summary>
public class DriverTopUpFailureVisibilityTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IDriverTopUpEventBroadcaster _broadcaster = Substitute.For<IDriverTopUpEventBroadcaster>();

    private (DriverTopUp TopUp, DriverWallet Wallet) PendingTopUp(decimal amount = 500m, string reference = "DRVTOPUP-1")
    {
        var driverId = Guid.NewGuid();
        var wallet = new DriverWallet(driverId);
        _repo.Wallets.Add(wallet);

        var topUp = new DriverTopUp(driverId, wallet.Id, amount, reference);
        topUp.SetProviderCheckout("paymongo", "cs_1", "https://checkout.url", null);
        _repo.TopUps.Add(topUp);

        return (topUp, wallet);
    }

    private RecordDriverTopUpPaymentFailureCommandHandler Handler()
        => new(_repo, _broadcaster);

    private static RecordDriverTopUpPaymentFailureCommand Decline(
        string reference = "DRVTOPUP-1",
        string paymentId = "pay_1",
        string reason = "Card declined")
        => new("paymongo", paymentId, reference, reason, DateTime.UtcNow);

    // --- The row the driver can actually see ---

    [Fact]
    public async Task A_declined_payment_is_recorded_as_a_failed_wallet_transaction()
    {
        var (topUp, wallet) = PendingTopUp(amount: 750m);

        var result = await Handler().Handle(Decline(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(_repo.Transactions);
        Assert.Equal(WalletTransactionStatus.Failed, row.Status);
        Assert.Equal(WalletTransactionType.TopUp, row.Type);
        Assert.Equal(WalletBucket.TopUp, row.Bucket);
        Assert.Equal(750m, row.Amount);
        Assert.Contains("Card declined", row.Description);
        Assert.Equal(wallet.Id, row.WalletId);
    }

    /// <summary>
    /// The whole point of allowing a Failed row into the ledger: it records an event, not a
    /// movement. If it ever moved a balance this feature would be a money bug.
    /// </summary>
    [Fact]
    public async Task A_declined_payment_moves_no_money()
    {
        var (_, wallet) = PendingTopUp(amount: 750m);

        await Handler().Handle(Decline(), CancellationToken.None);

        Assert.Equal(0m, wallet.TopUpBalance);
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(0m, wallet.PendingPayout);
    }

    /// <summary>
    /// A decline does not necessarily kill the provider's checkout session - the driver may pick
    /// another method and succeed on the same link. Closing the record here would break that.
    /// </summary>
    [Fact]
    public async Task A_declined_payment_leaves_the_top_up_open_for_a_retry()
    {
        var (topUp, _) = PendingTopUp();

        await Handler().Handle(Decline(), CancellationToken.None);

        Assert.Equal(DriverTopUpStatus.Pending, topUp.Status);
        Assert.Equal("Card declined", topUp.FailureReason);
    }

    [Fact]
    public async Task The_driver_is_told_immediately_rather_than_left_waiting()
    {
        var (topUp, _) = PendingTopUp(amount: 400m);

        await Handler().Handle(Decline(), CancellationToken.None);

        await _broadcaster.Received(1).PublishFailedAsync(
            Arg.Is<DriverTopUpFailedPayload>(p =>
                p.TopUpId == topUp.Id
                && p.DriverId == topUp.DriverId
                && p.Amount == 400m
                && p.CanRetry
                && p.Reason == "Card declined"),
            Arg.Any<CancellationToken>());
    }

    // --- Idempotency and noise control ---

    /// <summary>
    /// Webhooks redeliver. Keying the row on the provider's payment id means a repeat of the same
    /// attempt is a no-op, while a genuinely separate declined attempt still gets its own row.
    /// </summary>
    [Fact]
    public async Task A_redelivered_decline_does_not_stack_duplicate_rows()
    {
        PendingTopUp();

        await Handler().Handle(Decline(paymentId: "pay_1"), CancellationToken.None);
        await Handler().Handle(Decline(paymentId: "pay_1"), CancellationToken.None);

        Assert.Single(_repo.Transactions);
    }

    [Fact]
    public async Task Two_separate_declined_attempts_are_both_recorded()
    {
        PendingTopUp();

        await Handler().Handle(Decline(paymentId: "pay_1"), CancellationToken.None);
        await Handler().Handle(Decline(paymentId: "pay_2"), CancellationToken.None);

        Assert.Equal(2, _repo.Transactions.Count);
    }

    /// <summary>
    /// Booking payments fail too and reach the same hook. NotFound keeps them at Debug instead of
    /// filling the log with warnings about payments that were never top-ups.
    /// </summary>
    [Fact]
    public async Task A_declined_payment_that_is_not_a_top_up_reports_NotFound()
    {
        var result = await Handler().Handle(
            Decline(reference: "SOMETHING-ELSE", paymentId: "pay_booking"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorKind.NotFound, result.ErrorKind);
        Assert.Empty(_repo.Transactions);
    }

    /// <summary>
    /// The out-of-order case that would otherwise corrupt the record: attempt one is declined,
    /// the retry succeeds and credits the wallet, then the first decline is redelivered. It must
    /// not contradict a credited top-up.
    /// </summary>
    [Fact]
    public async Task A_decline_arriving_after_a_successful_retry_is_ignored()
    {
        var (topUp, wallet) = PendingTopUp();
        topUp.MarkAsPaid(DateTime.UtcNow);
        topUp.MarkCredited();
        wallet.AddTopUp(500m);

        var result = await Handler().Handle(Decline(paymentId: "pay_first"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(_repo.Transactions);
        Assert.Equal(DriverTopUpStatus.Paid, topUp.Status);
        Assert.Equal(500m, wallet.TopUpBalance);
    }

    // --- Operator recovery ---

    /// <summary>
    /// The endpoint that replaces hand-written SQL for a top-up stuck by an amount mismatch, a
    /// missing wallet, or a premature expiry.
    /// </summary>
    [Fact]
    public async Task An_admin_can_credit_a_stuck_top_up_with_an_audited_reason()
    {
        var (topUp, wallet) = PendingTopUp(amount: 500m);
        topUp.MarkAsExpired();

        var result = await new AdminCreditDriverTopUpCommandHandler(_repo).Handle(
            new AdminCreditDriverTopUpCommand(topUp.Id, null, "Paid but expired before reconciliation", "ops@bee"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(500m, wallet.TopUpBalance);
        Assert.NotNull(topUp.CreditedAt);

        var row = Assert.Single(_repo.Transactions);
        Assert.Equal(WalletTransactionStatus.Completed, row.Status);
        Assert.Contains("ops@bee", row.Description);
        Assert.Contains("Paid but expired before reconciliation", row.Description);
    }

    /// <summary>An amount mismatch is resolved by naming the amount the provider actually took.</summary>
    [Fact]
    public async Task An_admin_can_credit_the_amount_that_was_actually_paid()
    {
        var (topUp, wallet) = PendingTopUp(amount: 500m);

        await new AdminCreditDriverTopUpCommandHandler(_repo).Handle(
            new AdminCreditDriverTopUpCommand(topUp.Id, 480m, "Provider took 480", "ops@bee"),
            CancellationToken.None);

        Assert.Equal(480m, wallet.TopUpBalance);
    }

    /// <summary>A double-click must not credit twice, and must not look like an error either.</summary>
    [Fact]
    public async Task Crediting_an_already_credited_top_up_is_a_successful_no_op()
    {
        var (topUp, wallet) = PendingTopUp(amount: 500m);
        var handler = new AdminCreditDriverTopUpCommandHandler(_repo);
        var command = new AdminCreditDriverTopUpCommand(topUp.Id, null, "Manual recovery", "ops@bee");

        await handler.Handle(command, CancellationToken.None);
        var second = await handler.Handle(command, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(500m, wallet.TopUpBalance);
        Assert.Single(_repo.Transactions);
    }

    [Fact]
    public async Task A_manual_credit_requires_a_reason()
    {
        var (topUp, wallet) = PendingTopUp();

        var result = await new AdminCreditDriverTopUpCommandHandler(_repo).Handle(
            new AdminCreditDriverTopUpCommand(topUp.Id, null, "   ", "ops@bee"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0m, wallet.TopUpBalance);
    }
}
