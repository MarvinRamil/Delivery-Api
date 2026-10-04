using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <inheritdoc cref="IAbandonedCheckoutExpirer"/>
public sealed class AbandonedCheckoutExpirer : IAbandonedCheckoutExpirer
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly PaymentReconciliationOptions _options;
    private readonly ILogger<AbandonedCheckoutExpirer> _logger;

    public AbandonedCheckoutExpirer(
        IPaymentRepository paymentRepository,
        IPaymentGatewayFactory gatewayFactory,
        IOptions<PaymentReconciliationOptions> options,
        ILogger<AbandonedCheckoutExpirer> logger)
    {
        _paymentRepository = paymentRepository;
        _gatewayFactory = gatewayFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ExpireAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_options.ExpireAbandonedCheckouts)
            return 0;

        var cutoff = nowUtc.AddHours(-_options.AbandonedCheckoutGraceHours);
        var abandoned = await _paymentRepository.GetAbandonedPendingCheckoutsAsync(
            cutoff, _options.MaxCheckoutsExpiredPerRun, ct);

        if (abandoned.Count == 0)
            return 0;

        var expired = 0;
        foreach (var payment in abandoned)
        {
            try
            {
                // Route by the gateway that created the checkout, never the currently active one -
                // the same switch-safety invariant refunds follow.
                var gateway = _gatewayFactory.Get(payment.Provider);

                // Provider first, local row second. The reverse would leave a payable link alive at
                // the provider behind a record claiming it was dead - the worse failure, because
                // money could still arrive against a payment nothing is watching.
                await gateway.ExpireCheckoutAsync(payment.ProviderPaymentId!, ct);

                // No-ops if a webhook settled this payment in the meantime: MarkAsExpired refuses to
                // leave a terminal money state (GitLab #25).
                payment.MarkAsExpired();
                await _paymentRepository.SaveChangesAsync(ct);
                expired++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // A webhook wrote to this row between our read and our write - almost certainly the
                // customer paying at the last moment. Their settlement wins; leave it alone.
                _logger.LogInformation(
                    "[PaymentReconciliation] Skipped expiring PaymentId {PaymentId}: it changed underneath us (likely settled).",
                    payment.Id);
            }
            catch (Exception ex)
            {
                // One unreachable provider must not abandon the rest of the batch. The payment stays
                // Pending, so the next hourly run picks it up again.
                _logger.LogWarning(ex,
                    "[PaymentReconciliation] Could not expire checkout for PaymentId {PaymentId} ({Provider}/{ProviderPaymentId}).",
                    payment.Id, payment.Provider, payment.ProviderPaymentId);
            }
        }

        if (expired > 0)
            BeeMetrics.CheckoutsExpired.Add(expired);

        _logger.LogInformation(
            "[PaymentReconciliation] Expired {Expired}/{Found} abandoned checkout(s) untouched for over {Hours}h.",
            expired, abandoned.Count, _options.AbandonedCheckoutGraceHours);

        return expired;
    }
}
