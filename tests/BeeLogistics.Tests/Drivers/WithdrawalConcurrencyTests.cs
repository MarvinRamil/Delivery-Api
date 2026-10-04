using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Issue #90. The withdrawal create path used to call the provider BEFORE debiting the wallet,
/// so two devices could both pass the balance check on their own snapshots and both get a real
/// transfer executed, with only one surviving the save — a driver with PHP 500 could extract a
/// multiple of it, and the ledger stayed self-consistent afterwards so nothing tripped.
///
/// The order is now reserve-then-send: the wallet debit, the withdrawal row and the ledger row
/// commit in one SaveChanges, so the wallet's xmin check rejects the loser BEFORE any money can
/// move. These tests pin that ordering, and the refund rules that follow from it.
/// </summary>
public class WithdrawalConcurrencyTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly ISavedWithdrawalMethodRepository _savedMethods = Substitute.For<ISavedWithdrawalMethodRepository>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IDriverOutboxPublisher _outbox = Substitute.For<IDriverOutboxPublisher>();
    private static readonly Guid DriverId = Guid.NewGuid();

    public WithdrawalConcurrencyTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.PayMongo);
        _factory.GetActive().Returns(_gateway);
    }

    private RequestWithdrawalCommandHandler Handler() => new(
        _repo, _savedMethods, _factory, _outbox,
        Options.Create(new DriverWalletOptions()),
        NullLogger<RequestWithdrawalCommandHandler>.Instance);

    /// <summary>Wallet with a real balance, and the Earning row that justifies it so the
    /// integrity check reconciles.</summary>
    private DriverWallet SeedWallet(decimal balance)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.AddEarning(balance);
        _repo.Wallets.Add(wallet);
        _repo.Transactions.Add(new WalletTransaction(
            wallet.Id, WalletTransactionType.Earning, WalletBucket.Personal, balance,
            WalletTransactionStatus.Completed, "seed earning"));
        return wallet;
    }

    private static RequestWithdrawalCommand Command(decimal amount = 500m) => new(
        DriverId, amount,
        BankAccountNumber: "1234567890",
        BankName: "BPI",
        AccountHolderName: "Juan Cruz",
        BankCode: "BPI",
        IdempotencyKey: Guid.NewGuid().ToString());

    private static GatewayDisbursement Accepted() => new(
        "tr_1", "WD-x", GatewayDisbursementStatus.Pending, "pending", 500m, "BOPIPHMMXXX", "Juan Cruz");

    /// <summary>
    /// THE regression. The loser of the race must never reach the provider — if it does, a
    /// real transfer has been executed for money the wallet no longer has.
    /// </summary>
    [Fact]
    public async Task Losing_the_wallet_race_never_calls_the_provider()
    {
        SeedWallet(500m);
        _repo.FailNextReservationWithConcurrency = true;

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Another withdrawal is already being processed", result.Error);

        // The whole point: no transfer was created for the request that lost.
        await _gateway.DidNotReceiveWithAnyArgs().CreateDisbursementAsync(default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().ExecuteQrDisbursementAsync(default!, default);
    }

    [Fact]
    public async Task Funds_are_reserved_before_the_provider_is_called()
    {
        var wallet = SeedWallet(500m);
        decimal balanceWhenProviderWasCalled = -1m;

        _gateway.CreateDisbursementAsync(default!, default).ReturnsForAnyArgs(_ =>
        {
            // Captured at the moment of the call, proving the debit already happened.
            balanceWhenProviderWasCalled = wallet.Balance;
            return Task.FromResult(Accepted());
        });

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, balanceWhenProviderWasCalled);
        Assert.Equal(500m, wallet.PendingPayout);
        Assert.Single(_repo.Reservations);
    }

    [Fact]
    public async Task A_definite_provider_rejection_returns_the_money()
    {
        var wallet = SeedWallet(500m);
        _gateway.CreateDisbursementAsync(default!, default)
            .ThrowsAsyncForAnyArgs(new PaymentGatewayException(PaymentProviders.PayMongo, 400, "invalid_account", "bad account"));

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        // 4xx means the provider definitely did not take it, so the hold is released.
        Assert.Equal(500m, wallet.Balance);
        Assert.Equal(0m, wallet.PendingPayout);
        Assert.Equal(WithdrawalStatus.Failed, _repo.WithdrawalRequests.Single().Status);
    }

    [Fact]
    public async Task An_ambiguous_provider_failure_does_NOT_return_the_money()
    {
        var wallet = SeedWallet(500m);
        // A 503/timeout means we do not know whether the transfer exists. Refunding here would
        // let the driver withdraw the same money twice if it did.
        _gateway.CreateDisbursementAsync(default!, default)
            .ThrowsAsyncForAnyArgs(new PaymentGatewayException(PaymentProviders.PayMongo, 503, null, "gateway timeout"));

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(500m, wallet.PendingPayout);

        // Left as a reservation with no provider id — exactly what reconciliation looks for.
        var withdrawal = _repo.WithdrawalRequests.Single();
        Assert.Equal(WithdrawalStatus.Pending, withdrawal.Status);
        Assert.Null(withdrawal.ProviderDisbursementId);
    }

    [Fact]
    public async Task A_misconfigured_gateway_returns_the_money_and_never_holds_it()
    {
        var wallet = SeedWallet(500m);
        _gateway.CreateDisbursementAsync(default!, default)
            .ThrowsAsyncForAnyArgs(new PayoutsNotConfiguredException(PaymentProviders.PayMongo, "no source account"));

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("temporarily disabled", result.Error);
        Assert.Equal(500m, wallet.Balance);
        Assert.Equal(0m, wallet.PendingPayout);
    }

    [Fact]
    public async Task A_successful_payout_is_approved_and_carries_the_provider_id()
    {
        var wallet = SeedWallet(500m);
        _gateway.CreateDisbursementAsync(default!, default).ReturnsForAnyArgs(Accepted());

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var withdrawal = _repo.WithdrawalRequests.Single();
        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal("tr_1", withdrawal.ProviderDisbursementId);
        Assert.Equal(PaymentProviders.PayMongo, withdrawal.Provider);
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(500m, wallet.PendingPayout);
    }

    /// <summary>
    /// The business invariant, stated directly: however many withdrawals a driver fires, the
    /// total money that leaves must never exceed what was in the wallet. Covers both shapes —
    /// a genuine concurrent race, and a second attempt after the first already drained it.
    /// </summary>
    [Fact]
    public async Task Total_disbursed_never_exceeds_the_starting_balance()
    {
        const decimal startingBalance = 500m;
        var wallet = SeedWallet(startingBalance);

        var disbursed = 0m;
        _gateway.CreateDisbursementAsync(default!, default).ReturnsForAnyArgs(call =>
        {
            disbursed += call.Arg<CreateGatewayDisbursementRequest>().Amount;
            return Task.FromResult(Accepted());
        });

        // Device A wins the race and drains the wallet.
        var first = await Handler().Handle(Command(startingBalance), CancellationToken.None);
        Assert.True(first.IsSuccess);

        // Device B was in flight at the same time and loses on the wallet's xmin token.
        _repo.FailNextReservationWithConcurrency = true;
        var concurrent = await Handler().Handle(Command(startingBalance), CancellationToken.None);
        Assert.False(concurrent.IsSuccess);

        // Device C tries afterwards, reading the now-empty wallet.
        var afterwards = await Handler().Handle(Command(startingBalance), CancellationToken.None);
        Assert.False(afterwards.IsSuccess);

        // Three attempts, one payout, and not a centavo more than the driver had.
        Assert.Equal(startingBalance, disbursed);
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(startingBalance, wallet.PendingPayout);
        Assert.Single(_repo.Reservations);
    }

    [Fact]
    public async Task Insufficient_balance_is_still_rejected_before_anything_is_reserved()
    {
        SeedWallet(100m);

        var result = await Handler().Handle(Command(500m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(_repo.WithdrawalRequests);
        await _gateway.DidNotReceiveWithAnyArgs().CreateDisbursementAsync(default!, default);
    }
}

/// <summary>
/// Issue #90, second pass. The reconciliation paths that resolve a reservation whose provider
/// response was lost must keep the ACCOUNTING ledger balanced, not just the wallet.
///
/// WithdrawalFailedEvent is a reversal of WithdrawalRequestedEvent (credit DriverPersonalWallet,
/// debit PendingPayout). WithdrawalRequestedEvent is only published once the provider accepts.
/// So a reservation that never reached the provider has nothing to reverse, and one that DID
/// reach it needs its forward entry back-filled — otherwise the later Completed event debits a
/// PendingPayout that was never credited.
/// </summary>
public class WithdrawalReservationLedgerTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IDriverOutboxPublisher _outbox = Substitute.For<IDriverOutboxPublisher>();
    private static readonly Guid DriverId = Guid.NewGuid();

    private (WithdrawalRequest Withdrawal, DriverWallet Wallet) SeedReservation(decimal amount = 500m)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.AddEarning(amount);
        wallet.RequestWithdrawal(amount);          // funds held, as a reservation leaves them
        _repo.Wallets.Add(wallet);

        var withdrawal = new WithdrawalRequest(
            Guid.NewGuid(), DriverId, wallet.Id, amount, "123", "BPI", "Juan Cruz", autoApprove: false);
        _repo.WithdrawalRequests.Add(withdrawal);

        var tx = new WalletTransaction(
            wallet.Id, WalletTransactionType.Withdrawal, WalletBucket.Personal, amount,
            WalletTransactionStatus.Pending, "reservation", null, withdrawal.Id);
        _repo.Transactions.Add(tx);

        return (withdrawal, wallet);
    }

    [Fact]
    public async Task Releasing_a_reservation_returns_the_money_without_a_phantom_ledger_reversal()
    {
        var (withdrawal, wallet) = SeedReservation();

        var result = await new ReleaseWithdrawalReservationCommandHandler(
            _repo, _outbox, NullLogger<ReleaseWithdrawalReservationCommandHandler>.Instance)
            .Handle(new ReleaseWithdrawalReservationCommand(withdrawal.Id, "provider has no record"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Failed, withdrawal.Status);
        Assert.Equal(500m, wallet.Balance);      // returned to the driver
        Assert.Equal(0m, wallet.PendingPayout);

        // No Requested was ever published for this reservation, so a Failed reversal would
        // post a credit against nothing.
        _outbox.DidNotReceiveWithAnyArgs().Publish(default(WithdrawalFailedEvent)!);
    }

    [Fact]
    public async Task Adopting_a_found_transfer_backfills_the_forward_ledger_entry()
    {
        var (withdrawal, wallet) = SeedReservation();

        var result = await new AdoptWithdrawalDisbursementCommandHandler(
            _repo, _outbox, NullLogger<AdoptWithdrawalDisbursementCommandHandler>.Instance)
            .Handle(new AdoptWithdrawalDisbursementCommand(
                withdrawal.Id, PaymentProviders.PayMongo, "tr_found", 500m, "pending"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal("tr_found", withdrawal.ProviderDisbursementId);

        // The money left, so accounting needs the entry it missed when the response was lost.
        _outbox.ReceivedWithAnyArgs(1).Publish(default(WithdrawalRequestedEvent)!);

        // Funds stay held until the transfer actually settles.
        Assert.Equal(0m, wallet.Balance);
        Assert.Equal(500m, wallet.PendingPayout);
    }

    [Fact]
    public async Task A_transfer_whose_amount_disagrees_is_never_adopted()
    {
        var (withdrawal, _) = SeedReservation(500m);

        var result = await new AdoptWithdrawalDisbursementCommandHandler(
            _repo, _outbox, NullLogger<AdoptWithdrawalDisbursementCommandHandler>.Instance)
            .Handle(new AdoptWithdrawalDisbursementCommand(
                withdrawal.Id, PaymentProviders.PayMongo, "tr_someone_else", 4200m, "pending"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Pending, withdrawal.Status);
        Assert.Null(withdrawal.ProviderDisbursementId);
        _outbox.DidNotReceiveWithAnyArgs().Publish(default(WithdrawalRequestedEvent)!);
    }
}
