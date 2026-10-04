using BeeLogistics.Modules.Accounting.Application.Consumers;
using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Accounting;

public class CashDeficitAdjustedAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _repository = Substitute.For<ILedgerEntryRepository>();

    private CashDeficitAdjustedAccountingConsumer Consumer() =>
        new(_repository, NullLogger<CashDeficitAdjustedAccountingConsumer>.Instance);

    private static ConsumeContext<CashDeficitAdjustedEvent> ContextFor(CashDeficitAdjustedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<CashDeficitAdjustedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task A_credit_debits_the_suspense_account_and_credits_the_wallet()
    {
        var msg = new CashDeficitAdjustedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150m, "Disputed debit", "admin@bee.ph", DateTime.UtcNow);

        await Consumer().Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverTopUpWallet && !e.IsDebit && e.Amount == 150m) &&
                entries.Any(e => e.AccountCode == AccountCode.ManualAdjustment && e.IsDebit && e.Amount == 150m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_debit_credits_the_suspense_account_and_debits_the_wallet()
    {
        var msg = new CashDeficitAdjustedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), -75m, "Confirmed shortfall", "admin@bee.ph", DateTime.UtcNow);

        await Consumer().Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverTopUpWallet && e.IsDebit && e.Amount == 75m) &&
                entries.Any(e => e.AccountCode == AccountCode.ManualAdjustment && !e.IsDebit && e.Amount == 75m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_is_skipped()
    {
        var msg = new CashDeficitAdjustedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 150m, "Disputed debit", "admin@bee.ph", DateTime.UtcNow);
        _repository.ExistsByIdempotencyKeyAsync($"cash-deficit-adjustment-{msg.AdjustmentId}", Arg.Any<CancellationToken>())
            .Returns(true);

        await Consumer().Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }
}
