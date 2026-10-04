using BeeLogistics.Modules.Accounting.Application.Consumers;
using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Accounting;

public class DriverTopUpCreditedAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _repository = Substitute.For<ILedgerEntryRepository>();

    private DriverTopUpCreditedAccountingConsumer Consumer() =>
        new(_repository, NullLogger<DriverTopUpCreditedAccountingConsumer>.Instance);

    private static ConsumeContext<DriverTopUpCreditedEvent> ContextFor(DriverTopUpCreditedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<DriverTopUpCreditedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Debits_top_up_payment_and_credits_top_up_wallet()
    {
        var msg = new DriverTopUpCreditedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m, "xendit", DateTime.UtcNow);

        await Consumer().Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverTopUpPayment && e.IsDebit && e.Amount == 500m) &&
                entries.Any(e => e.AccountCode == AccountCode.DriverTopUpWallet && !e.IsDebit && e.Amount == 500m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_is_skipped()
    {
        var msg = new DriverTopUpCreditedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m, "xendit", DateTime.UtcNow);
        _repository.ExistsByIdempotencyKeyAsync($"topup-{msg.TopUpId}", Arg.Any<CancellationToken>())
            .Returns(true);

        await Consumer().Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }
}
