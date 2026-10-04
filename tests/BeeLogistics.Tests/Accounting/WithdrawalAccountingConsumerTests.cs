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
/// The three withdrawal consumers had no test coverage at all, despite relying purely on
/// application-level check-then-insert against LedgerEntries.IdempotencyKey - the same
/// check-then-insert pattern already found and fixed on the driver-wallet debit path. The DB-level
/// backstop is now a real unique index (see AddLedgerEntryIdempotencyKeyUniqueIndex); these cover
/// the application-level guard each consumer is still responsible for getting right.
/// </summary>
public class WithdrawalAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _repository = Substitute.For<ILedgerEntryRepository>();

    private static ConsumeContext<T> ContextFor<T>(T msg) where T : class
    {
        var ctx = Substitute.For<ConsumeContext<T>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Requested_debits_wallet_and_credits_pending_payout()
    {
        var msg = new WithdrawalRequestedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", DateTime.UtcNow, null);

        await new WithdrawalRequestedAccountingConsumer(_repository, NullLogger<WithdrawalRequestedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverPersonalWallet && e.IsDebit && e.Amount == 500m) &&
                entries.Any(e => e.AccountCode == AccountCode.PendingPayout && !e.IsDebit && e.Amount == 500m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requested_redelivery_is_skipped()
    {
        var msg = new WithdrawalRequestedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", DateTime.UtcNow, null);
        _repository.ExistsByIdempotencyKeyAsync($"withdrawal-{msg.WithdrawalId}-requested", Arg.Any<CancellationToken>())
            .Returns(true);

        await new WithdrawalRequestedAccountingConsumer(_repository, NullLogger<WithdrawalRequestedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_debits_pending_payout_and_credits_xendit_out()
    {
        var msg = new WithdrawalCompletedEvent(Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", DateTime.UtcNow);

        await new WithdrawalCompletedAccountingConsumer(_repository, NullLogger<WithdrawalCompletedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.PendingPayout && e.IsDebit && e.Amount == 500m) &&
                entries.Any(e => e.AccountCode == AccountCode.XenditOut && !e.IsDebit && e.Amount == 500m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Completed_redelivery_is_skipped()
    {
        var msg = new WithdrawalCompletedEvent(Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", DateTime.UtcNow);
        _repository.ExistsByIdempotencyKeyAsync($"withdrawal-{msg.WithdrawalId}-completed", Arg.Any<CancellationToken>())
            .Returns(true);

        await new WithdrawalCompletedAccountingConsumer(_repository, NullLogger<WithdrawalCompletedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_reverses_debit_back_to_wallet()
    {
        var msg = new WithdrawalFailedEvent(Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", "insufficient float", DateTime.UtcNow);

        await new WithdrawalFailedAccountingConsumer(_repository, NullLogger<WithdrawalFailedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Any(e => e.AccountCode == AccountCode.DriverPersonalWallet && !e.IsDebit && e.Amount == 500m) &&
                entries.Any(e => e.AccountCode == AccountCode.PendingPayout && e.IsDebit && e.Amount == 500m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_redelivery_is_skipped()
    {
        var msg = new WithdrawalFailedEvent(Guid.NewGuid(), Guid.NewGuid(), 500m, "PHP", "insufficient float", DateTime.UtcNow);
        _repository.ExistsByIdempotencyKeyAsync($"withdrawal-{msg.WithdrawalId}-failed", Arg.Any<CancellationToken>())
            .Returns(true);

        await new WithdrawalFailedAccountingConsumer(_repository, NullLogger<WithdrawalFailedAccountingConsumer>.Instance)
            .Consume(ContextFor(msg));

        await _repository.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }
}
