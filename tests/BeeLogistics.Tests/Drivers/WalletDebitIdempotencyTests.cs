using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The platform's share of a cash delivery, swept from the driver's own PayMongo wallet to the
/// platform's (issue #102).
///
/// <para>
/// On a cash job the customer pays the driver directly, so the driver walks away holding our
/// commission. This used to be booked as a debit against the Cash Wallet float — an inactive
/// feature nobody funds — so the fee was recorded and never collected. The money is in the driver's
/// child wallet, so that is where it is taken from.
/// </para>
/// <para>
/// The properties inherited from the old suite still hold and are still tested here: idempotency
/// keyed on the booking id (a redelivery must not charge twice), the row and the balance committed
/// in one save, the ledger event, and the database's own unique constraint as the backstop.
/// </para>
/// </summary>
public class WalletDebitIdempotencyTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IDriverOutboxPublisher _outbox = Substitute.For<IDriverOutboxPublisher>();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();
    private static readonly Guid DriverId = Guid.NewGuid();

    public WalletDebitIdempotencyTests() => SweepSucceeds();

    private ApplyCashSettlementDebitCommandHandler Handler() => new(
        _repo,
        Options.Create(new DriverWalletOptions()),
        _outbox,
        _accounts);

    /// <summary>A driver holding their balance in their own PayMongo wallet, funded.</summary>
    private DriverWallet Wallet(decimal balance = 1000m)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "817797809438", "ledger_1");
        if (balance > 0) wallet.AddBeeWalletTopUp(balance);
        _repo.Wallets.Add(wallet);
        return wallet;
    }

    private void SweepSucceeds() =>
        _accounts.SweepFromChildAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoInternalTransfer("tr_1", "succeeded", ci.ArgAt<decimal>(3), 0m, null));

    private static WalletTransaction Commission(FakeDriverWalletRepository repo) =>
        Assert.Single(repo.Transactions.Where(t => t.Type == WalletTransactionType.PlatformCommission));

    [Fact]
    public async Task The_commission_is_swept_from_the_driver_wallet_to_the_platform()
    {
        var wallet = Wallet(balance: 1000m);

        await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        // The money moved, so the mirror moved with it.
        Assert.Equal(975m, wallet.Balance);

        var commission = Commission(_repo);
        Assert.Equal(WalletTransactionStatus.Completed, commission.Status);
        Assert.Equal("tr_1", commission.ProviderPaymentId);
        Assert.Equal(WalletBucket.Personal, commission.Bucket);

        // Never the Cash Wallet: it is not an active feature, and booking revenue against a bucket
        // nobody funds is exactly what this replaced.
        Assert.Equal(0m, wallet.TopUpBalance);
    }

    /// <summary>
    /// The regression that matters. Idempotency used to key on a formatted description; this keys
    /// on the booking id instead, so a redelivery of the same booking's event must not charge twice.
    /// </summary>
    [Fact]
    public async Task Redelivering_the_same_booking_charges_only_once()
    {
        var wallet = Wallet();
        var bookingId = Guid.NewGuid();
        var handler = Handler();

        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);
        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);

        Assert.Single(_repo.Transactions);
        Assert.Equal(975m, wallet.Balance);
        await _accounts.Received(1).SweepFromChildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Two distinct bookings are two genuine charges.</summary>
    [Fact]
    public async Task Two_different_bookings_both_charge()
    {
        var wallet = Wallet();
        var handler = Handler();

        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);
        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.Equal(2, _repo.Transactions.Count);
        Assert.Equal(950m, wallet.Balance);
    }

    /// <summary>
    /// A balance change may never be committed without its ledger row, or vice versa. The repository
    /// writes both in one SaveChanges, so a failure there commits neither and the command must fail
    /// — which is what puts the message back through redelivery instead of leaving a half-applied
    /// charge that the next retry reads as settled.
    /// </summary>
    /// <remarks>
    /// Asserts the outcome, not the in-memory wallet: the entity was already mutated before the save
    /// was attempted, and it is the discarded scope — not a rollback of the object — that undoes it.
    /// The row staying Pending in the database is what the retry actually depends on.
    /// </remarks>
    [Fact]
    public async Task A_failed_write_fails_the_command_so_it_is_redelivered()
    {
        Wallet();
        _repo.FailNextSaveClaimedTransaction = true;

        var result = await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// A wallet that cannot cover the fee is left owing it rather than driven negative — a child
    /// wallet cannot hold a negative balance, and a negative mirror would read to the reconciler as
    /// money PayMongo is holding and be credited straight back.
    /// </summary>
    [Fact]
    public async Task A_balance_short_of_the_commission_leaves_it_owed_and_takes_nothing()
    {
        var wallet = Wallet(balance: 10m);

        var result = await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.True(result.IsSuccess);          // the delivery happened; refusing would lose the fee
        Assert.Equal(10m, wallet.Balance);      // all-or-nothing: not even a partial 10 is taken
        Assert.Equal(WalletTransactionStatus.Pending, Commission(_repo).Status);

        await _accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }

    /// <summary>What is owed is the ledger — there is no balance column that could disagree.</summary>
    [Fact]
    public async Task An_owed_commission_is_retried_on_the_next_cash_job()
    {
        var wallet = Wallet(balance: 10m);
        var handler = Handler();

        await handler.Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);
        Assert.Single(await _repo.GetUnpaidCommissionsAsync(wallet.Id));

        // They top up, then finish another job: the old fee is collected before the new one.
        wallet.AddBeeWalletTopUp(500m);
        await handler.Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 30m), CancellationToken.None);

        Assert.Empty(await _repo.GetUnpaidCommissionsAsync(wallet.Id));
        Assert.Equal(455m, wallet.Balance);     // 510 − 25 arrears − 30 current
        Assert.Equal(2, _repo.Transactions.Count(t => t.Type == WalletTransactionType.PlatformCommission));
    }

    /// <summary>
    /// A rejected transfer leaves a definite outcome, not an ambiguous gap: the money never left, so
    /// the mirror must not move, and the row is marked Failed rather than deleted.
    /// </summary>
    [Fact]
    public async Task A_rejected_sweep_leaves_the_balance_alone_and_fails_the_command()
    {
        var wallet = Wallet();
        _accounts.SweepFromChildAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_1", "failed", 0m, 0m, "insufficient balance"));

        var result = await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, wallet.Balance);
        Assert.Equal(WalletTransactionStatus.Failed, Commission(_repo).Status);
    }

    /// <summary>
    /// A provider outage must be retried, never swallowed: the command fails so the message goes
    /// back through redelivery, and the row is still there to make the retry idempotent.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_fails_the_command_so_it_is_redelivered()
    {
        var wallet = Wallet();
        _accounts.SweepFromChildAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PaymentGatewayException("PayMongo", 503, "unavailable", "upstream down"));

        var result = await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, wallet.Balance);
        Assert.Equal(WalletTransactionStatus.Pending, Commission(_repo).Status);
    }

    /// <summary>
    /// Unreachable in practice — dispatch will not offer a cash job to a driver with no wallet — but
    /// the delivery has already happened, so the fee is recorded rather than lost. Emphatically not
    /// charged to the Cash Wallet.
    /// </summary>
    [Fact]
    public async Task A_driver_without_a_BeeWallet_is_left_owing_rather_than_charged_elsewhere()
    {
        var wallet = new DriverWallet(DriverId);
        _repo.Wallets.Add(wallet);

        var result = await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(0m, wallet.TopUpBalance);
        Assert.Equal(WalletTransactionStatus.Pending, Commission(_repo).Status);
    }

    /// <summary>
    /// The database-level backstop, independent of the application check: a second row for the same
    /// wallet/booking/type must be refused outright.
    /// </summary>
    [Fact]
    public async Task The_unique_constraint_refuses_a_second_row_for_the_same_booking_and_type()
    {
        var wallet = Wallet();
        var bookingId = Guid.NewGuid();
        await Handler().Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);

        var duplicate = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.PlatformCommission,
            WalletBucket.Personal,
            25m,
            WalletTransactionStatus.Completed,
            $"Platform commission for booking {bookingId}",
            bookingId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CreateTransactionAsync(duplicate));
    }

    /// <summary>
    /// The sweep is real money movement, so it must reach the ledger the same way earnings and
    /// withdrawals do — see CashSettlementDebitedAccountingConsumerTests for the ledger side.
    /// </summary>
    [Fact]
    public async Task A_successful_sweep_publishes_the_ledger_event()
    {
        Wallet();
        var bookingId = Guid.NewGuid();

        await Handler().Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);

        _outbox.Received(1).Publish(Arg.Is<CashSettlementDebitedEvent>(e =>
            e.BookingId == bookingId && e.DriverId == DriverId && e.Amount == 25m));
    }

    [Fact]
    public async Task A_redelivered_sweep_does_not_publish_the_ledger_event_again()
    {
        Wallet();
        var bookingId = Guid.NewGuid();
        var handler = Handler();

        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);
        await handler.Handle(new ApplyCashSettlementDebitCommand(DriverId, bookingId, 25m), CancellationToken.None);

        _outbox.Received(1).Publish(Arg.Any<CashSettlementDebitedEvent>());
    }

    /// <summary>A fee that was never collected keeps the driver off cash jobs until it is.</summary>
    [Fact]
    public async Task An_unpaid_commission_makes_the_driver_ineligible_for_cash_jobs()
    {
        var wallet = Wallet(balance: 10m);
        Assert.True(wallet.IsEligibleForCashJobs(hasUnpaidCommission: false));

        await Handler().Handle(
            new ApplyCashSettlementDebitCommand(DriverId, Guid.NewGuid(), 25m), CancellationToken.None);

        Assert.True(await _repo.HasUnpaidCommissionAsync(wallet.Id));
        Assert.False(wallet.IsEligibleForCashJobs(hasUnpaidCommission: true));
    }
}
