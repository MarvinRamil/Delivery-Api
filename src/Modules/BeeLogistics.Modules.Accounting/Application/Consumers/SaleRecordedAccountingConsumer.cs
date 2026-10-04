using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes SaleRecordedEvent (cash and cashless). Records ledger entries and the fast Sales table.
/// Idempotent by BookingId.
/// </summary>
public class SaleRecordedAccountingConsumer : IConsumer<SaleRecordedEvent>
{
    private readonly ILedgerEntryRepository _ledgerRepository;
    private readonly ISalesEntryRepository _salesRepository;
    private readonly IAccountingUnitOfWork _unitOfWork;
    private readonly ILogger<SaleRecordedAccountingConsumer> _logger;

    public SaleRecordedAccountingConsumer(
        ILedgerEntryRepository ledgerRepository,
        ISalesEntryRepository salesRepository,
        IAccountingUnitOfWork unitOfWork,
        ILogger<SaleRecordedAccountingConsumer> logger)
    {
        _ledgerRepository = ledgerRepository;
        _salesRepository = salesRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SaleRecordedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (await _salesRepository.GetByBookingIdAsync(msg.BookingId, ct) != null)
        {
            _logger.LogInformation("[Accounting] SaleRecorded idempotent skip - BookingId: {BookingId}", msg.BookingId);
            return;
        }

        // Double-entry invariant: the single debit must equal the sum of the credits. If the
        // producer ever computes the platform/driver split with mismatched rounding again, the
        // books would silently drift a centavo per booking - refuse the write instead so it
        // surfaces as a failed message rather than as an unexplained ledger imbalance later.
        var credits = msg.PlatformCommissionAmount + msg.DriverAmount;
        if (credits != msg.Amount)
        {
            _logger.LogError(
                "[Accounting] Refusing unbalanced sale - BookingId: {BookingId}, Debit: {Debit}, Credits: {Credits} (commission {Commission} + driver {Driver})",
                msg.BookingId, msg.Amount, credits, msg.PlatformCommissionAmount, msg.DriverAmount);
            throw new InvalidOperationException(
                $"Unbalanced SaleRecordedEvent for booking {msg.BookingId}: debit {msg.Amount} != credits {credits}.");
        }

        var refId = msg.BookingId.ToString();
        var currency = "PHP";
        var idempotencyKey = $"sale-{msg.BookingId}";

        // Ledger written but SalesEntry missing means a previous attempt died between the two
        // commits. Returning here would make that permanent, because the sales row is only ever
        // written below - so fall through and let the transaction complete the pair.
        var ledgerAlreadyWritten = await _ledgerRepository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct);
        if (ledgerAlreadyWritten)
        {
            _logger.LogWarning(
                "[Accounting] Ledger entries exist without a SalesEntry for BookingId {BookingId} - completing the missing sales row.",
                msg.BookingId);
        }

        // Double-entry: Debit CustomerPayment (money in), Credit PlatformRevenue, Credit DriverPersonalWallet
        var debitCustomer = LedgerEntry.Create(
            AccountCode.CustomerPayment,
            isDebit: true,
            msg.Amount,
            currency,
            "Sale",
            refId,
            $"Sale booking {msg.BookingId:N} ({msg.PaymentMethod})",
            null,
            idempotencyKey
        );
        var creditPlatform = LedgerEntry.Create(
            AccountCode.PlatformRevenue,
            isDebit: false,
            msg.PlatformCommissionAmount,
            currency,
            "Sale",
            refId,
            $"Platform commission booking {msg.BookingId:N}",
            msg.DriverId,
            null
        );
        var creditDriver = LedgerEntry.Create(
            AccountCode.DriverPersonalWallet,
            isDebit: false,
            msg.DriverAmount,
            currency,
            "Sale",
            refId,
            $"Driver earning booking {msg.BookingId:N}",
            msg.DriverId,
            null
        );

        var salesEntry = SalesEntry.Create(
            msg.BookingId,
            msg.CompletedAtUtc,
            msg.Amount,
            currency,
            msg.PaymentMethod,
            msg.DriverId,
            msg.CustomerId,
            msg.PlatformCommissionAmount,
            msg.DriverAmount
        );

        // Both writes commit together or neither does, so the ledger and the sales table can no
        // longer disagree after a mid-consumer failure.
        await _unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            if (!ledgerAlreadyWritten)
                await _ledgerRepository.AddRangeAsync(new[] { debitCustomer, creditPlatform, creditDriver }, token);

            await _salesRepository.AddAsync(salesEntry, token);
        }, ct);

        _logger.LogInformation("[Accounting] Recorded sale - BookingId: {BookingId}, Amount: {Amount}, Method: {Method}",
            msg.BookingId, msg.Amount, msg.PaymentMethod);
    }
}
