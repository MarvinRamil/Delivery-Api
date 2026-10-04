using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes WithdrawalRequestedEvent and records ledger entries (debit driver wallet, credit pending payout).
/// Non-blocking; runs asynchronously via MassTransit. Idempotent by IdempotencyKey.
/// </summary>
public class WithdrawalRequestedAccountingConsumer : IConsumer<WithdrawalRequestedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<WithdrawalRequestedAccountingConsumer> _logger;

    public WithdrawalRequestedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<WithdrawalRequestedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WithdrawalRequestedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var idempotencyKey = !string.IsNullOrWhiteSpace(msg.IdempotencyKey)
            ? $"wd-{msg.IdempotencyKey}"
            : $"withdrawal-{msg.WithdrawalId}-requested";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] WithdrawalRequested idempotent skip - WithdrawalId: {Id}", msg.WithdrawalId);
            return;
        }

        var refId = msg.WithdrawalId.ToString();
        var debit = LedgerEntry.Create(
            AccountCode.DriverPersonalWallet,
            isDebit: true,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal requested WD-{msg.WithdrawalId:N}",
            msg.DriverId,
            idempotencyKey
        );
        var credit = LedgerEntry.Create(
            AccountCode.PendingPayout,
            isDebit: false,
            msg.Amount,
            msg.Currency,
            "Withdrawal",
            refId,
            $"Withdrawal requested WD-{msg.WithdrawalId:N}",
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { debit, credit }, ct);
        _logger.LogInformation("[Accounting] Recorded withdrawal requested - WithdrawalId: {Id}, Amount: {Amount}", msg.WithdrawalId, msg.Amount);
    }
}
