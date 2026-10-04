using BeeLogistics.Modules.Accounting.Application.Consumers;
using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Accounting;

/// <summary>
/// The double-entry balance guard added for GitLab #28.
///
/// The ledger writes one debit (the full sale) against two credits (platform commission and
/// driver earning). If the producer computes those with mismatched rounding, the books drift a
/// centavo per booking and nothing notices until someone reconciles by hand. The consumer now
/// refuses the write instead.
/// </summary>
public class SaleRecordedAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _ledger = Substitute.For<ILedgerEntryRepository>();
    private readonly ISalesEntryRepository _sales = Substitute.For<ISalesEntryRepository>();
    private readonly IAccountingUnitOfWork _unitOfWork = new PassThroughUnitOfWork();

    private SaleRecordedAccountingConsumer Consumer() =>
        new(_ledger, _sales, _unitOfWork, NullLogger<SaleRecordedAccountingConsumer>.Instance);

    /// <summary>
    /// Runs the work inline. The transaction boundary itself needs a real database to test, so
    /// these tests cover what happens inside it; atomicity is asserted by the migration/SQL
    /// review rather than here.
    /// </summary>
    private sealed class PassThroughUnitOfWork : IAccountingUnitOfWork
    {
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
            => work(ct);
    }

    private static ConsumeContext<SaleRecordedEvent> ContextFor(SaleRecordedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<SaleRecordedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static SaleRecordedEvent Sale(decimal amount, decimal commission, decimal driver) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), amount, "Cashless", commission, driver, DateTime.UtcNow);

    [Fact]
    public async Task Balanced_sale_is_recorded()
    {
        var split = EarningsSplit.For(100.10m, 0.05m);
        var msg = Sale(100.10m, split.CommissionAmount, split.DriverAmount);

        await Consumer().Consume(ContextFor(msg));

        await _ledger.Received(1).AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
        await _sales.Received(1).AddAsync(Arg.Any<SalesEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 5.01 + 95.10 = 100.11, which is exactly what the two old formulas produced for a
    /// 100.10 fare. Nothing may be written when the entries do not balance.
    /// </summary>
    [Fact]
    public async Task Unbalanced_sale_is_refused_and_writes_nothing()
    {
        var msg = Sale(amount: 100.10m, commission: 5.01m, driver: 95.10m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Consumer().Consume(ContextFor(msg)));

        await _ledger.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
        await _sales.DidNotReceive().AddAsync(Arg.Any<SalesEntry>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The ledger and the SalesEntry used to be committed separately, so a failure in between
    /// left the ledger written and the sales row missing. The retry then hit the ledger's
    /// idempotency key and returned early — making the gap permanent. It must now complete the
    /// missing row instead.
    /// </summary>
    [Fact]
    public async Task Ledger_written_without_a_sales_row_is_repaired_not_skipped()
    {
        var split = EarningsSplit.For(500m, 0.05m);
        var msg = Sale(500m, split.CommissionAmount, split.DriverAmount);
        // Ledger present (previous attempt got this far), sales row absent (it died here).
        _ledger.ExistsByIdempotencyKeyAsync($"sale-{msg.BookingId}", Arg.Any<CancellationToken>()).Returns(true);
        _sales.GetByBookingIdAsync(msg.BookingId, Arg.Any<CancellationToken>()).Returns((SalesEntry?)null);

        await Consumer().Consume(ContextFor(msg));

        await _sales.Received(1).AddAsync(Arg.Any<SalesEntry>(), Arg.Any<CancellationToken>());
        // ...and must not double-write the ledger it already has.
        await _ledger.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Credits_exceeding_the_debit_are_refused()
    {
        var msg = Sale(amount: 100m, commission: 5m, driver: 96m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Consumer().Consume(ContextFor(msg)));
    }

    /// <summary>
    /// The guard must not fire before the idempotency check, or a redelivery of an already
    /// recorded sale would start throwing instead of skipping.
    /// </summary>
    [Fact]
    public async Task Already_recorded_sale_is_still_skipped_quietly()
    {
        var split = EarningsSplit.For(500m, 0.05m);
        var msg = Sale(500m, split.CommissionAmount, split.DriverAmount);
        _sales.GetByBookingIdAsync(msg.BookingId, Arg.Any<CancellationToken>())
            .Returns(SalesEntry.Create(msg.BookingId, msg.CompletedAtUtc, msg.Amount, "PHP",
                msg.PaymentMethod, msg.DriverId, msg.CustomerId, msg.PlatformCommissionAmount, msg.DriverAmount));

        await Consumer().Consume(ContextFor(msg));

        await _ledger.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
        await _sales.DidNotReceive().AddAsync(Arg.Any<SalesEntry>(), Arg.Any<CancellationToken>());
    }
}
