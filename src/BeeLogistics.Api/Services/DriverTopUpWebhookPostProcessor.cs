using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Runs synchronously when a payment checkout webhook is received so driver top-up wallet
/// is credited immediately without relying on the message bus.
/// </summary>
public sealed class DriverTopUpWebhookPostProcessor : IPaymentWebhookPostProcessor
{
    private readonly IMediator _mediator;
    private readonly ILogger<DriverTopUpWebhookPostProcessor> _logger;

    public DriverTopUpWebhookPostProcessor(IMediator mediator, ILogger<DriverTopUpWebhookPostProcessor> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ProcessAsync(string provider, string providerPaymentId, string status, DateTime? paidAt, decimal? paidAmount, string? externalId, string? currency = null, CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new ProcessDriverTopUpWebhookCommand(provider, providerPaymentId, status, paidAt, paidAmount, externalId, currency),
            ct);

        if (result.IsSuccess)
        {
            _logger.LogInformation("Driver top-up credited for checkout {ProviderPaymentId} (sync)", providerPaymentId);
            return;
        }

        // NotFound is the ordinary case: booking-payment webhooks reach this post-processor too.
        // Any other failure means a real top-up went uncredited, which is money we are holding.
        if (result.ErrorKind == ResultErrorKind.NotFound)
            _logger.LogDebug("Checkout {ProviderPaymentId} is not a driver top-up; skipping.", providerPaymentId);
        else
            _logger.LogWarning("Driver top-up for checkout {ProviderPaymentId} was not credited: {Reason}", providerPaymentId, result.Error);
    }

    public async Task ProcessFailedAsync(string provider, string providerPaymentId, string? externalId, string? reason, DateTime? failedAt, CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new RecordDriverTopUpPaymentFailureCommand(provider, providerPaymentId, externalId, reason, failedAt),
            ct);

        if (result.IsSuccess)
            return;

        // NotFound just means the declined payment was not a top-up - booking payments fail too.
        if (result.ErrorKind == ResultErrorKind.NotFound)
            _logger.LogDebug("Declined payment {ProviderPaymentId} is not a driver top-up; skipping.", providerPaymentId);
        else
            _logger.LogWarning("Could not record declined top-up payment {ProviderPaymentId}: {Reason}", providerPaymentId, result.Error);
    }
}
