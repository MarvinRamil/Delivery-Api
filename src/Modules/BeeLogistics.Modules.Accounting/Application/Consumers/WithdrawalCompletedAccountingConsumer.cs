using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes WithdrawalCompletedEvent and records ledger entries (debit PendingPayout, credit XenditOut).
/// Idempotent by reference so replay does not double-record.
/// </summary>
public class WithdrawalCompletedAccountingConsumer : IConsumer<WithdrawalCompletedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<WithdrawalCompletedAccountingConsumer> _logger;

    public WithdrawalCompletedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<WithdrawalCompletedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WithdrawalCompletedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var idempotencyKey = $"withdrawal-{msg.WithdrawalId}-completed";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] WithdrawalCompleted idempotent skip - WithdrawalId: {Id}", msg.WithdrawalId);
            return;
        }

        var refId = msg.WithdrawalId.ToString();
        var debit = LedgerEntry.Create(
            AccountCode.PendingPayout,
            isDebit: true,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal completed WD-{msg.WithdrawalId:N}",
            msg.DriverId,
            idempotencyKey
        );
        var credit = LedgerEntry.Create(
            AccountCode.XenditOut,
            isDebit: false,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal completed WD-{msg.WithdrawalId:N}",
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { debit, credit }, ct);
        _logger.LogInformation("[Accounting] Recorded withdrawal completed - WithdrawalId: {Id}, Amount: {Amount}", msg.WithdrawalId, msg.Amount);
    }
}
