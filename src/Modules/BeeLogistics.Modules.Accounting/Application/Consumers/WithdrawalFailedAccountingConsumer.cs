using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes WithdrawalFailedEvent and records reversal entries (credit DriverPersonalWallet, debit PendingPayout).
/// Idempotent by reference so replay does not double-record.
/// </summary>
public class WithdrawalFailedAccountingConsumer : IConsumer<WithdrawalFailedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<WithdrawalFailedAccountingConsumer> _logger;

    public WithdrawalFailedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<WithdrawalFailedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WithdrawalFailedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var idempotencyKey = $"withdrawal-{msg.WithdrawalId}-failed";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] WithdrawalFailed idempotent skip - WithdrawalId: {Id}", msg.WithdrawalId);
            return;
        }

        var refId = msg.WithdrawalId.ToString();
        // Reversal: credit driver wallet (credit = negative debit in our convention), debit pending payout
        var creditWallet = LedgerEntry.Create(
            AccountCode.DriverPersonalWallet,
            isDebit: false,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal failed reversal WD-{msg.WithdrawalId:N}: {msg.Reason}",
            msg.DriverId,
            idempotencyKey
        );
        var debitPayout = LedgerEntry.Create(
            AccountCode.PendingPayout,
            isDebit: true,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal failed reversal WD-{msg.WithdrawalId:N}",
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { creditWallet, debitPayout }, ct);
        _logger.LogInformation("[Accounting] Recorded withdrawal failed - WithdrawalId: {Id}, Amount: {Amount}", msg.WithdrawalId, msg.Amount);
    }
}
