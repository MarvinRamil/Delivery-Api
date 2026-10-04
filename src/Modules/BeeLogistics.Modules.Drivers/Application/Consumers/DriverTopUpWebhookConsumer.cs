using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Consumers;

public class DriverTopUpWebhookConsumer : IConsumer<PaymentCheckoutWebhookReceived>
{
    private readonly IMediator _mediator;
    private readonly ILogger<DriverTopUpWebhookConsumer> _logger;

    public DriverTopUpWebhookConsumer(IMediator mediator, ILogger<DriverTopUpWebhookConsumer> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentCheckoutWebhookReceived> context)
    {
        var msg = context.Message;
        _logger.LogInformation(
            "Processing driver top-up webhook event. Provider: {Provider}, ProviderPaymentId: {ProviderPaymentId}, ExternalId: {ExternalId}, Status: {Status}",
            msg.Provider,
            msg.ProviderPaymentId,
            msg.ExternalId,
            msg.Status);

        var result = await _mediator.Send(
            new ProcessDriverTopUpWebhookCommand(msg.Provider, msg.ProviderPaymentId, msg.Status, msg.PaidAt, msg.PaidAmount, msg.ExternalId),
            context.CancellationToken);

        if (!result.IsSuccess)
        {
            // NotFound means this checkout was never a top-up - almost always a booking payment,
            // which reaches this consumer too. Anything else means we found the top-up and
            // refused to credit it, which is money we are holding and must not be Debug-only.
            if (result.ErrorKind == ResultErrorKind.NotFound)
                _logger.LogDebug(
                    "Checkout {ProviderPaymentId} is not a driver top-up; ignoring.",
                    msg.ProviderPaymentId);
            else
                _logger.LogWarning(
                    "Driver top-up webhook for checkout {ProviderPaymentId} was not credited. Reason: {Reason}",
                    msg.ProviderPaymentId,
                    result.Error);
            return;
        }

        _logger.LogInformation("Driver top-up webhook applied for checkout {ProviderPaymentId}", msg.ProviderPaymentId);
    }
}
