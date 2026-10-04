using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes CashDeficitAdjustedEvent and records ledger entries against AccountCode.ManualAdjustment
/// (a suspense account, not a real external one), so an admin's manual wallet correction is
/// ledger-covered the same way every automated wallet-balance change is. Idempotent by AdjustmentId.
/// </summary>
public class CashDeficitAdjustedAccountingConsumer : IConsumer<CashDeficitAdjustedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<CashDeficitAdjustedAccountingConsumer> _logger;

    public CashDeficitAdjustedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<CashDeficitAdjustedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<CashDeficitAdjustedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.SignedAmount == 0)
            return;

        var idempotencyKey = $"cash-deficit-adjustment-{msg.AdjustmentId}";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] CashDeficitAdjusted idempotent skip - AdjustmentId: {AdjustmentId}", msg.AdjustmentId);
            return;
        }

        var amount = Math.Abs(msg.SignedAmount);
        var isCredit = msg.SignedAmount > 0;
        var refId = msg.AdjustmentId.ToString();
        var description = $"Manual cash deficit adjustment by {msg.PerformedBy}: {msg.Reason}";

        var walletEntry = LedgerEntry.Create(
            AccountCode.DriverTopUpWallet,
            isDebit: !isCredit,
            amount,
            "PHP",
            "ManualAdjustment",
            refId,
            description,
            msg.DriverId,
            idempotencyKey
        );
        var suspenseEntry = LedgerEntry.Create(
            AccountCode.ManualAdjustment,
            isDebit: isCredit,
            amount,
            "PHP",
            "ManualAdjustment",
            refId,
            description,
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { walletEntry, suspenseEntry }, ct);
        _logger.LogInformation(
            "[Accounting] Recorded manual cash deficit adjustment - AdjustmentId: {AdjustmentId}, SignedAmount: {SignedAmount}",
            msg.AdjustmentId, msg.SignedAmount);
    }
}
