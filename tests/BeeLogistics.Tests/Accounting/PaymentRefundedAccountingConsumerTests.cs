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
/// A refund used to leave the original SaleRecordedAccountingConsumer entries standing forever -
/// the ledger kept claiming a sale that no longer stood. This consumer posts the mirror image,
/// keyed on the original SalesEntry so the reversal always agrees with what was actually booked.
/// </summary>
public class PaymentRefundedAccountingConsumerTests
{
    private readonly ILedgerEntryRepository _ledger = Substitute.For<ILedgerEntryRepository>();
    private readonly ISalesEntryRepository _sales = Substitute.For<ISalesEntryRepository>();

    private PaymentRefundedAccountingConsumer Consumer() =>
        new(_ledger, _sales, NullLogger<PaymentRefundedAccountingConsumer>.Instance);

    private static ConsumeContext<PaymentRefundedEvent> ContextFor(PaymentRefundedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<PaymentRefundedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private SalesEntry Sale(Guid bookingId, decimal amount, decimal commission, decimal driver)
    {
        var sale = SalesEntry.Create(bookingId, DateTime.UtcNow, amount, "PHP", "Cashless",
            Guid.NewGuid(), Guid.NewGuid(), commission, driver);
        _sales.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(sale);
        return sale;
    }

    [Fact]
    public async Task A_full_refund_reverses_the_exact_original_split()
    {
        var bookingId = Guid.NewGuid();
        Sale(bookingId, amount: 500m, commission: 25m, driver: 475m);
        var msg = new PaymentRefundedEvent { BookingId = bookingId, DriverId = Guid.NewGuid(), AmountRefunded = 500m };

        await Consumer().Consume(ContextFor(msg));

        await _ledger.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Count() == 3 &&
                entries.Any(e => e.AccountCode == AccountCode.CustomerPayment && !e.IsDebit && e.Amount == 500m) &&
                entries.Any(e => e.AccountCode == AccountCode.PlatformRevenue && e.IsDebit && e.Amount == 25m) &&
                entries.Any(e => e.AccountCode == AccountCode.DriverPersonalWallet && e.IsDebit && e.Amount == 475m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_partial_refund_prorates_against_the_original_split()
    {
        var bookingId = Guid.NewGuid();
        Sale(bookingId, amount: 500m, commission: 25m, driver: 475m);
        var msg = new PaymentRefundedEvent { BookingId = bookingId, DriverId = Guid.NewGuid(), AmountRefunded = 200m };

        await Consumer().Consume(ContextFor(msg));

        // 200/500 = 40% of the original split: commission 10m, driver 190m. Must still sum to 200.
        await _ledger.Received(1).AddRangeAsync(
            Arg.Is<IEnumerable<LedgerEntry>>(entries =>
                entries.Sum(e => e.IsDebit ? -e.Amount : e.Amount) == 0m &&
                entries.Any(e => e.AccountCode == AccountCode.PlatformRevenue && e.Amount == 10m) &&
                entries.Any(e => e.AccountCode == AccountCode.DriverPersonalWallet && e.Amount == 190m)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_does_not_double_reverse()
    {
        var bookingId = Guid.NewGuid();
        Sale(bookingId, amount: 500m, commission: 25m, driver: 475m);
        _ledger.ExistsByIdempotencyKeyAsync($"refund-{bookingId}", Arg.Any<CancellationToken>()).Returns(true);
        var msg = new PaymentRefundedEvent { BookingId = bookingId, DriverId = Guid.NewGuid(), AmountRefunded = 500m };

        await Consumer().Consume(ContextFor(msg));

        await _ledger.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_matching_sale_writes_nothing()
    {
        var msg = new PaymentRefundedEvent { BookingId = Guid.NewGuid(), DriverId = Guid.NewGuid(), AmountRefunded = 200m };

        await Consumer().Consume(ContextFor(msg));

        await _ledger.DidNotReceive().AddRangeAsync(Arg.Any<IEnumerable<LedgerEntry>>(), Arg.Any<CancellationToken>());
    }
}
