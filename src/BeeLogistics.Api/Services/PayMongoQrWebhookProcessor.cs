using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Routes PayMongo <c>qr.paid</c> webhooks to the Drivers module, keeping the Payment module free
/// of wallet logic — the same arrangement as <see cref="PayMongoAccountWebhookProcessor"/>.
/// </summary>
public sealed class PayMongoQrWebhookProcessor : IPayMongoQrWebhookProcessor
{
    private readonly IMediator _mediator;
    private readonly ILogger<PayMongoQrWebhookProcessor> _logger;

    public PayMongoQrWebhookProcessor(IMediator mediator, ILogger<PayMongoQrWebhookProcessor> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ProcessAsync(string? creditAccountNumber, string? accountId, decimal amount,
        string idempotencyKey, string? referenceLabel = null, CancellationToken ct = default)
    {
        // A cashbond QR credits the PLATFORM wallet, not a driver's child wallet, so the only link
        // back to the driver is the reference label, and it carries the transaction id. Checked
        // first: without it this payment would fall through to the top-up path, find no wallet for
        // the platform's own account, and be dropped.
        if (CreateCashBondQrCommandHandler.TransactionIdFrom(referenceLabel) is not null)
        {
            var cashBond = await _mediator.Send(
                new SettleCashBondQrPaymentCommand(referenceLabel!, amount, idempotencyKey), ct);

            if (!cashBond.IsSuccess)
            {
                _logger.LogWarning(
                    "[PAYMONGO] [QR] Could not settle cashbond {Reference}: {Error}",
                    referenceLabel, cashBond.Error);
            }
            return;
        }

        // A package-insurance premium QR also credits the PLATFORM wallet, so it needs the same
        // reference-label routing as cashbond, checked before the top-up fallback below.
        if (CreateInsurancePremiumQrCommandHandler.TransactionIdFrom(referenceLabel) is not null)
        {
            var insurance = await _mediator.Send(
                new SettleInsurancePremiumQrPaymentCommand(referenceLabel!, amount, idempotencyKey), ct);

            if (!insurance.IsSuccess)
            {
                _logger.LogWarning(
                    "[PAYMONGO] [QR] Could not settle package-insurance premium {Reference}: {Error}",
                    referenceLabel, insurance.Error);
            }
            return;
        }

        var result = await _mediator.Send(
            new CreditBeeWalletTopUpCommand(creditAccountNumber, accountId, amount, idempotencyKey), ct);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "[PAYMONGO] [QR] Could not credit payment {Key}: {Error}", idempotencyKey, result.Error);
        }
    }
}
