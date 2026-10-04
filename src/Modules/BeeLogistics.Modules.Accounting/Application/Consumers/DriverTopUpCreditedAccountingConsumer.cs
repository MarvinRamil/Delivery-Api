using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Accounting.Application.Consumers;

/// <summary>
/// Consumes DriverTopUpCreditedEvent and records ledger entries (debit DriverTopUpPayment, credit
/// DriverTopUpWallet) - real cash received from the driver, so unlike the cash-settlement entry
/// this is a plain, unambiguous mirror of CustomerPayment/DriverPersonalWallet for the top-up
/// domain. Idempotent by TopUpId.
/// </summary>
public class DriverTopUpCreditedAccountingConsumer : IConsumer<DriverTopUpCreditedEvent>
{
    private readonly ILedgerEntryRepository _repository;
    private readonly ILogger<DriverTopUpCreditedAccountingConsumer> _logger;

    public DriverTopUpCreditedAccountingConsumer(
        ILedgerEntryRepository repository,
        ILogger<DriverTopUpCreditedAccountingConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<DriverTopUpCreditedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        if (msg.Amount <= 0)
            return;

        var idempotencyKey = $"topup-{msg.TopUpId}";
        if (await _repository.ExistsByIdempotencyKeyAsync(idempotencyKey, ct))
        {
            _logger.LogInformation("[Accounting] DriverTopUpCredited idempotent skip - TopUpId: {TopUpId}", msg.TopUpId);
            return;
        }

        var refId = msg.TopUpId.ToString();
        var debitPayment = LedgerEntry.Create(
            AccountCode.DriverTopUpPayment,
            isDebit: true,
            msg.Amount,
            "PHP",
            "TopUp",
            refId,
            $"Top-up via {msg.Provider} checkout, driver {msg.DriverId:N}",
            msg.DriverId,
            idempotencyKey
        );
        var creditWallet = LedgerEntry.Create(
            AccountCode.DriverTopUpWallet,
            isDebit: false,
            msg.Amount,
            "PHP",
            "TopUp",
            refId,
            $"Top-up credited, driver {msg.DriverId:N}",
            msg.DriverId,
            null
        );

        await _repository.AddRangeAsync(new[] { debitPayment, creditWallet }, ct);
        _logger.LogInformation(
            "[Accounting] Recorded driver top-up credit - TopUpId: {TopUpId}, Amount: {Amount}",
            msg.TopUpId, msg.Amount);
    }
}
