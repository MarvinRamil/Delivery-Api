using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes CashSettlementDebitedEvent and records ledger entries (debit DriverTopUpWallet,
/// credit CustomerPayment). Idempotent by BookingId.
///
/// The commission on a cash booking is already recognized as PlatformRevenue at sale time via
/// SaleRecordedEvent, regardless of payment method - this entry is not additional revenue, it's
/// the driver's TopUp wallet actually settling the slice of that already-booked receivable that
/// cash bypassed. Crediting CustomerPayment here reduces the receivable booked at sale time rather
/// than posting new revenue, keeping the pair balanced without a new receivable account. This
/// mapping is a first pass; treat the exact account choice as open to finance review.
/// </summary>
public class CashSettlementDebitedAccountingConsumer : IConsumer<CashSettlementDebitedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<CashSettlementDebitedAccountingConsumer> _logger;

    public CashSettlementDebitedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<CashSettlementDebitedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CashSettlementDebitedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.Amount <= 0)
            return;

        var idempotencyKey = $"cash-settlement-{msg.BookingId}";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] CashSettlementDebited idempotent skip - BookingId: {BookingId}", msg.BookingId);
            return;
        }

        var refId = msg.BookingId.ToString();
        var debitTopUp = LedgerEntry.Create(
            AccountCode.DriverTopUpWallet,
            isDebit: true,
            msg.Amount,
            "PHP",
            "CashSettlement",
            refId,
            $"Cash settlement debit for booking {msg.BookingId:N}",
            msg.DriverId,
            idempotencyKey
        );
        var creditCustomer = LedgerEntry.Create(
            AccountCode.CustomerPayment,
            isDebit: false,
            msg.Amount,
            "PHP",
            "CashSettlement",
            refId,
            $"Cash settlement collected via driver TopUp wallet, booking {msg.BookingId:N}",
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { debitTopUp, creditCustomer }, ct);
        _logger.LogInformation(
            "[Accounting] Recorded cash settlement debit - BookingId: {BookingId}, Amount: {Amount}",
            msg.BookingId, msg.Amount);
    }
}
