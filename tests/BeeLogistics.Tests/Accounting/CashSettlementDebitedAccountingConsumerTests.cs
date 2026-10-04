using BeeLogistics.Modules.Accounting.Application.Consumers;
using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Accounting;

/// <summary>
/// The real TopUp-wallet debit for cash bookings had no ledger representation at all before this
/// consumer existed, so AccountCode.DriverTopUpWallet's reported balance was always zero.
/// </summary>
public class CashSettlementDebitedAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _repository = Substitute.For<ILedgerEntryRepository>();

    private CashSettlementDebitedAccountingConsumer Consumer() =>
        new(_repository, NullLogger<CashSettlementDebitedAccountingConsumer>.Instance);

    private static ConsumeContext<CashSettlementDebitedEvent> ContextFor(CashSettlementDebitedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<CashSettlementDebitedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Debits_top_up_wallet_and_credits_customer_payment()
    {
        var msg = new CashSettlementDebitedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 25m, DateTime.UtcNow);

        await Consumer().Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverTopUpWallet && e.IsDebit && e.Amount == 25m) &&
                entries.Any(e => e.AccountCode == AccountCode.CustomerPayment && !e.IsDebit && e.Amount == 25m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_is_skipped()
    {
        var msg = new CashSettlementDebitedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 25m, DateTime.UtcNow);
        _repository.ExistsByIdempotencyKeyAsync($"cash-settlement-{msg.BookingId}", Arg.Any<CancellationToken>())
            .Returns(true);

        await Consumer().Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }
}
