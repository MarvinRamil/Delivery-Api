using BeeLogistics.Shared.Contracts;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Processes disbursement/payout webhooks by sending the command to the Drivers module.
/// Updates withdrawal status, wallet PendingPayout, and publishes accounting events.
/// </summary>
public sealed class DisbursementWebhookProcessor : IDisbursementWebhookProcessor
{
    private readonly IMediator _mediator;
    private readonly ILogger<DisbursementWebhookProcessor> _logger;

    public DisbursementWebhookProcessor(IMediator mediator, ILogger<DisbursementWebhookProcessor> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ProcessAsync(string provider, string disbursementId, string eventType, string status, string? failureReason, CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new ProcessDisbursementWebhookCommand(provider, disbursementId, eventType, status, failureReason),
            ct);

        if (result.IsSuccess)
            _logger.LogInformation("Disbursement webhook processed for {DisbursementId} ({Event}, {Status})", disbursementId, eventType, status);
        else
            _logger.LogWarning("Disbursement webhook processing failed for {DisbursementId}: {Reason}", disbursementId, result.Error);
    }
}
