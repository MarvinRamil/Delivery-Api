using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes PaymentRefundedEvent and posts the mirror image of the ledger entries
/// SaleRecordedAccountingConsumer wrote at booking completion (credit CustomerPayment, debit
/// PlatformRevenue, debit DriverPersonalWallet), so a refund stops leaving the ledger claiming a
/// sale that no longer stands.
///
/// The refunded amount is split proportionally against the original SalesEntry's recorded
/// commission/driver amounts (not recomputed against today's commission rate), so the reversal
/// always agrees with what was actually booked even if the rate has since changed.
///
/// Idempotent by BookingId, matching the granularity of the existing driver-wallet reversal
/// (Drivers/PaymentRefundedEventConsumer): a second partial refund against the same booking is
/// not separately reversed here today. Widening this to one reversal per refund, rather than per
/// booking, needs PaymentRefundedEvent to carry its own refund id - out of scope for this pass.
/// </summary>
public class PaymentRefundedAccountingConsumer : IConsumer<PaymentRefundedEvent>
{
    private readonly ILedgerEntryRepository _ledgerRepository;
    private readonly ISalesEntryRepository _salesRepository;
    private readonly ILogger<PaymentRefundedAccountingConsumer> _logger;

    public PaymentRefundedAccountingConsumer(
        ILedgerEntryRepository ledgerRepository,
        ISalesEntryRepository salesRepository,
        ILogger<PaymentRefundedAccountingConsumer> logger)
    {
        _ledgerRepository = ledgerRepository;
        _salesRepository = salesRepository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentRefundedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.AmountRefunded <= 0)
            return;

        var idempotencyKey = $"refund-{msg.BookingId}";
        if (await _ledgerRepository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] PaymentRefunded idempotent skip - BookingId: {BookingId}", msg.BookingId);
            return;
        }

        var sale = await _salesRepository.GetByBookingIdAsync(msg.BookingId, ct);
        if (sale == null)
        {
            // No original sale on the books (e.g. the booking never completed, or the event
            // arrived before SaleRecordedAccountingConsumer processed its own). Nothing to
            // reverse yet - not an error, just missing information the same as the driver-wallet
            // consumer's own "was this ever credited" guard.
            _logger.LogWarning(
                "[Accounting] PaymentRefundedEvent for BookingId {BookingId} has no matching SalesEntry - skipping ledger reversal.",
                msg.BookingId);
            return;
        }

        // Prorate against what was actually recorded at sale time, not today's commission rate.
        // Only the commission side is rounded; the driver side takes the exact remainder, so the
        // pair always sums to msg.AmountRefunded - the same invariant SaleRecordedAccountingConsumer
        // enforces on the way in.
        var ratio = sale.Amount == 0 ? 0m : msg.AmountRefunded / sale.Amount;
        var commissionAmount = decimal.Round(sale.PlatformCommissionAmount * ratio, 2, MidpointRounding.AwayFromZero);
        var driverAmount = msg.AmountRefunded - commissionAmount;

        var refId = msg.BookingId.ToString();
        var currency = sale.Currency;

        var entries = new List<LedgerEntry>
        {
            LedgerEntry.Create(
                AccountCode.CustomerPayment,
                isDebit: false,
                msg.AmountRefunded,
                currency,
                "Refund",
                refId,
                $"Refund for booking {msg.BookingId:N}",
                null,
                idempotencyKey)
        };

        // LedgerEntry.Create refuses a zero amount, and a centavo-level refund can legitimately
        // round one side of the split to zero. Skipping that entry (rather than posting a zero
        // row) still leaves the pair balanced, since the sum is unaffected either way.
        if (commissionAmount > 0)
            entries.Add(LedgerEntry.Create(
                AccountCode.PlatformRevenue,
                isDebit: true,
                commissionAmount,
                currency,
                "Refund",
                refId,
                $"Platform commission reversal, booking {msg.BookingId:N}",
                msg.DriverId,
                null));

        if (driverAmount > 0)
            entries.Add(LedgerEntry.Create(
                AccountCode.DriverPersonalWallet,
                isDebit: true,
                driverAmount,
                currency,
                "Refund",
                refId,
                $"Driver earning reversal, booking {msg.BookingId:N}",
                msg.DriverId,
                null));

        await _ledgerRepository.AddRangeAsync(entries, ct);
        _logger.LogInformation(
            "[Accounting] Recorded refund reversal - BookingId: {BookingId}, Amount: {Amount}",
            msg.BookingId, msg.AmountRefunded);
    }
}
