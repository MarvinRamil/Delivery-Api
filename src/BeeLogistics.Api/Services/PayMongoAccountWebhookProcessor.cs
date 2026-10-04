using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Routes PayMongo child-account lifecycle webhooks to the Drivers module, keeping the Payment
/// module free of wallet logic — the same arrangement as <see cref="DisbursementWebhookProcessor"/>.
/// </summary>
public sealed class PayMongoAccountWebhookProcessor : IPayMongoAccountWebhookProcessor
{
    private readonly IMediator _mediator;
    private readonly ILogger<PayMongoAccountWebhookProcessor> _logger;

    public PayMongoAccountWebhookProcessor(IMediator mediator, ILogger<PayMongoAccountWebhookProcessor> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ProcessAsync(string accountId, string eventType, string? activationStatus,
        string? failureReason = null, CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new SyncPayMongoAccountStatusCommand(accountId, eventType, activationStatus, failureReason), ct);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "[PAYMONGO] [ACCOUNT] {EventType} for {AccountId} could not be applied: {Error}",
                eventType, accountId, result.Error);
        }
    }
}
