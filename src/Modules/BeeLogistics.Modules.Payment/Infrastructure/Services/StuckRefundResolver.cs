using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <inheritdoc cref="IStuckRefundResolver"/>
public sealed class StuckRefundResolver : IStuckRefundResolver
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly IMediator _mediator;
    private readonly ILogger<StuckRefundResolver> _logger;

    public StuckRefundResolver(
        IPaymentRepository paymentRepository,
        IPaymentGatewayFactory gatewayFactory,
        IMediator mediator,
        ILogger<StuckRefundResolver> logger)
    {
        _paymentRepository = paymentRepository;
        _gatewayFactory = gatewayFactory;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<int> ResolveAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        var stuck = await _paymentRepository.GetStuckRefundPendingPaymentsAsync(olderThanUtc, ct);
        if (stuck.Count == 0)
            return 0;

        var resolved = 0;
        foreach (var payment in stuck)
        {
            if (string.IsNullOrEmpty(payment.ProviderRefundId))
            {
                // Reserved but never reached the provider (e.g. a captureId lookup failed before
                // CreateRefundAsync) - ReleaseReservationAsync already returns these to Paid, so a
                // row still RefundPending with no provider refund id at all shouldn't occur. Skip
                // rather than guess at a provider call with nothing to poll.
                _logger.LogWarning(
                    "[StuckRefundResolver] PaymentId {PaymentId} is RefundPending with no ProviderRefundId - cannot poll, skipping.",
                    payment.Id);
                continue;
            }

            try
            {
                var gateway = _gatewayFactory.Get(payment.Provider);
                var refund = await gateway.GetRefundAsync(payment.ProviderRefundId, ct);
                if (refund == null)
                {
                    // Provider has no record of it, or the poll itself failed (already logged by
                    // the adapter) - still unknown either way. Leave it for the next run.
                    continue;
                }

                // Reuse the exact same finalization path a real refund.* webhook would take:
                // idempotent lookup by (Provider, ProviderRefundId), atomic publish+save on
                // success. This is deliberately not reimplemented here.
                var result = await _mediator.Send(
                    new ProcessRefundWebhookCommand(payment.Provider, payment.ProviderRefundId, refund.RawStatus, null),
                    ct);

                if (result.IsSuccess)
                {
                    resolved++;
                }
                else
                {
                    _logger.LogWarning(
                        "[StuckRefundResolver] Could not finalize PaymentId {PaymentId} from polled refund {ProviderRefundId}: {Error}",
                        payment.Id, payment.ProviderRefundId, result.Error);
                }
            }
            catch (Exception ex)
            {
                // One unreachable/misbehaving provider must not abandon the rest of the batch -
                // same reasoning as AbandonedCheckoutExpirer. The payment stays RefundPending, so
                // the next run picks it up again.
                _logger.LogWarning(ex,
                    "[StuckRefundResolver] Could not resolve PaymentId {PaymentId} ({Provider}/{ProviderRefundId}).",
                    payment.Id, payment.Provider, payment.ProviderRefundId);
            }
        }

        _logger.LogInformation(
            "[StuckRefundResolver] Resolved {Resolved}/{Found} stuck refund(s).", resolved, stuck.Count);

        return resolved;
    }
}
