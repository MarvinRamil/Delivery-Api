using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Before CreateCashDeficitAdjustmentCommandHandler existed, a driver disputing an automated
/// CashSettlementDebit - or a genuine under/over remittance - had no correction path at all:
/// WalletTransactionType.CashDeficitAdjustment existed but nothing ever created one.
/// </summary>
public class CashDeficitAdjustmentTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IDriverOutboxPublisher _outbox = Substitute.For<IDriverOutboxPublisher>();
    private static readonly Guid DriverId = Guid.NewGuid();

    private CreateCashDeficitAdjustmentCommandHandler Handler() => new(_repo, _outbox);

    private DriverWallet Wallet(decimal startingTopUpBalance = 0m)
    {
        var wallet = new DriverWallet(DriverId);
        if (startingTopUpBalance != 0m)
            wallet.AddTopUp(startingTopUpBalance); // only supports positive seeding; fine for these tests
        _repo.Wallets.Add(wallet);
        return wallet;
    }

    [Fact]
    public async Task A_positive_amount_credits_the_driver()
    {
        var wallet = Wallet();

        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, 150m, "Disputed cash settlement debit", "admin@bee.ph"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(150m, wallet.TopUpBalance);
    }

    [Fact]
    public async Task A_negative_amount_debits_the_driver()
    {
        var wallet = Wallet(startingTopUpBalance: 100m);

        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, -40m, "Confirmed shortfall", "admin@bee.ph"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(60m, wallet.TopUpBalance);
    }

    /// <summary>
    /// Unlike the automated cash-settlement debit, an admin write-off must be able to push the
    /// wallet past the -500 floor - that floor exists to stop new debt accruing automatically, not
    /// to block a human correcting the books.
    /// </summary>
    [Fact]
    public async Task Does_not_enforce_the_negative_limit_floor()
    {
        var wallet = Wallet();

        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, -600m, "Writing off unrecoverable debt", "admin@bee.ph"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(-600m, wallet.TopUpBalance);
    }

    [Fact]
    public async Task An_empty_reason_is_rejected()
    {
        Wallet();

        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, 100m, "  ", "admin@bee.ph"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(_repo.Transactions);
    }

    [Fact]
    public async Task A_zero_amount_is_rejected()
    {
        Wallet();

        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, 0m, "no-op", "admin@bee.ph"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Creates_a_wallet_when_the_driver_has_none_yet()
    {
        var result = await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, 100m, "Onboarding correction", "admin@bee.ph"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(_repo.Wallets);
        Assert.Equal(100m, _repo.Wallets[0].TopUpBalance);
    }

    [Fact]
    public async Task Publishes_the_ledger_event_with_the_signed_amount_and_reason()
    {
        Wallet();

        await Handler().Handle(
            new CreateCashDeficitAdjustmentCommand(DriverId, -75m, "Confirmed shortfall", "admin@bee.ph"),
            CancellationToken.None);

        _outbox.Received(1).Publish(Arg.Is<CashDeficitAdjustedEvent>(e =>
            e.DriverId == DriverId &&
            e.SignedAmount == -75m &&
            e.Reason == "Confirmed shortfall" &&
            e.PerformedBy == "admin@bee.ph"));
    }
}
