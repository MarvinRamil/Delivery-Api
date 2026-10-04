using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Infrastructure;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Api.Services;

public class DriverTopUpReconciliationService
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly IMediator _mediator;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<DriverTopUpReconciliationService> _logger;

    public DriverTopUpReconciliationService(
        IDriverWalletRepository walletRepository,
        IPaymentGatewayFactory gatewayFactory,
        IMediator mediator,
        IOptions<DriverWalletOptions> options,
        ILogger<DriverTopUpReconciliationService> logger)
    {
        _walletRepository = walletRepository;
        _gatewayFactory = gatewayFactory;
        _mediator = mediator;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Reconciliation job: checks pending top-ups that may have been missed by webhooks, then
    /// re-checks recently expired ones that were never credited.
    ///
    /// Only considers records at least 15 minutes old, to give webhooks time to arrive.
    /// </summary>
    /// <remarks>
    /// DisableConcurrentExecution because a run makes one provider round-trip per candidate with
    /// a deliberate pause between each, so a slow provider can easily overrun the schedule.
    /// Overlapping runs would double-spend the API budget on the same records.
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 30, 120 })]
    public async Task ReconcilePaidTopUpsAsync()
    {
        // Only check invoices created at least 15 minutes ago (give webhooks time to arrive)
        var minAgeForReconciliation = TimeSpan.FromMinutes(15);
        var cutoffTime = DateTime.UtcNow - minAgeForReconciliation;

        var batchSize = _options.TopUpReconciliationBatchSize > 0 ? _options.TopUpReconciliationBatchSize : 50;

        // Oldest first, and only records that carry a provider checkout id. Both conditions are
        // enforced in SQL: the pending pool is every abandoned checkout of the last day, so
        // taking the newest would re-check recent records forever and never reach the stuck ones.
        var topUpsToCheck = await _walletRepository.GetTopUpsAwaitingReconciliationAsync(cutoffTime, batchSize);

        if (topUpsToCheck.Count == 0)
        {
            _logger.LogDebug("No pending top-ups older than {Minutes} minutes to reconcile", minAgeForReconciliation.TotalMinutes);
        }
        else
        {
            if (topUpsToCheck.Count == batchSize)
            {
                _logger.LogInformation(
                    "Reconciliation checking the {Checked} oldest pending top-ups (per-run cap reached; the remainder are newer and stay ahead of the queue next run)",
                    topUpsToCheck.Count);
            }

            var (repairedCount, errorCount) = await CheckAgainstProviderAsync(topUpsToCheck, "pending");

            if (repairedCount > 0)
                _logger.LogWarning("Driver top-up reconciliation credited {Count} missed top-ups (webhooks may have been missed)", repairedCount);

            if (errorCount > 0)
                _logger.LogWarning("Reconciliation encountered {ErrorCount} errors while processing {Total} top-ups", errorCount, topUpsToCheck.Count);

            if (repairedCount == 0 && errorCount == 0)
                _logger.LogDebug("Reconciliation completed: checked {Count} pending top-ups, all still pending", topUpsToCheck.Count);
        }

        await RecoverExpiredButPaidTopUpsAsync();
    }

    /// <summary>
    /// Second pass: top-ups the expiry job already closed but that were never credited.
    ///
    /// Expiry runs on a purely local timer and never asks the provider, and the pass above only
    /// looks at Pending records. Without this, a driver who paid just before the deadline - or
    /// whose record was still queued behind others - has the payment written off permanently
    /// with nothing left to notice it.
    /// </summary>
    private async Task RecoverExpiredButPaidTopUpsAsync()
    {
        if (_options.ExpiredTopUpRecheckHours <= 0)
            return;

        var createdSince = DateTime.UtcNow.AddHours(-_options.ExpiredTopUpRecheckHours);
        var batchSize = _options.ExpiredTopUpRecheckBatchSize > 0 ? _options.ExpiredTopUpRecheckBatchSize : 25;

        var expired = await _walletRepository.GetUncreditedExpiredTopUpsAsync(createdSince, batchSize);
        if (expired.Count == 0)
            return;

        var (recovered, errors) = await CheckAgainstProviderAsync(expired, "expired");

        if (recovered > 0)
        {
            BeeMetrics.DriverTopUpsRecoveredAfterExpiry.Add(recovered);
            _logger.LogWarning(
                "Recovered {Count} top-ups that had been closed as Expired but were actually paid. The local expiry timer is writing off real money.",
                recovered);
        }

        if (errors > 0)
            _logger.LogWarning("Expired top-up recovery encountered {ErrorCount} errors while checking {Total} records", errors, expired.Count);
    }

    /// <summary>
    /// Asks the provider about each candidate and credits any that turn out to be paid.
    /// Returns (credited, errored). Never throws: one bad record must not stop the batch.
    /// </summary>
    private async Task<(int Credited, int Errored)> CheckAgainstProviderAsync(
        IReadOnlyList<DriverTopUp> candidates,
        string pass)
    {
        var credited = 0;
        var errored = 0;

        foreach (var topUp in candidates)
        {
            // Guaranteed non-null by the queries, which filter it in SQL; belt and braces.
            if (string.IsNullOrWhiteSpace(topUp.ProviderPaymentId))
                continue;

            try
            {
                // Small delay between API calls to be respectful to the provider
                await Task.Delay(200, CancellationToken.None);

                // Resolve the gateway per record so top-ups created before a
                // gateway switch still reconcile against their original provider.
                var gateway = _gatewayFactory.Get(topUp.Provider);

                var session = await gateway.GetCheckoutAsync(topUp.ProviderPaymentId);
                if (session == null)
                {
                    _logger.LogDebug("Checkout {ProviderPaymentId} not found at provider for top-up {TopUpId} ({Pass} pass)",
                        topUp.ProviderPaymentId, topUp.Id, pass);
                    continue;
                }

                if (session.Status != GatewayPaymentStatus.Paid)
                    continue;

                var result = await _mediator.Send(
                    new ProcessDriverTopUpWebhookCommand(topUp.Provider, session.ProviderPaymentId, session.RawStatus, session.PaidAt, session.Amount, session.ReferenceId));

                if (result.IsSuccess)
                {
                    credited++;
                    _logger.LogInformation("Reconciliation credited missed top-up {TopUpId} (checkout {ProviderPaymentId}, {Pass} pass)",
                        topUp.Id, session.ProviderPaymentId, pass);
                }
                else
                {
                    // The provider says this was paid and we would not credit it. That is money
                    // we are holding, so it is a warning with a metric, not a debug line.
                    BeeMetrics.DriverTopUpsUncredited.Add(1, new KeyValuePair<string, object?>("reason", "reconcile_failed"));
                    _logger.LogWarning(
                        "Provider reports top-up {TopUpId} (checkout {ProviderPaymentId}) as paid but it could not be credited: {Error}. Needs manual resolution.",
                        topUp.Id, topUp.ProviderPaymentId, result.Error);
                }
            }
            catch (Exception ex)
            {
                errored++;
                _logger.LogError(ex, "Error reconciling top-up {TopUpId} (checkout {ProviderPaymentId}, {Pass} pass)",
                    topUp.Id, topUp.ProviderPaymentId, pass);
                // Continue processing other records even if one fails
            }
        }

        return (credited, errored);
    }

    /// <summary>
    /// Auto-close unpaid top-ups: those past invoice expiry or created long ago without an expiry.
    /// Runs on a schedule (e.g. every 15 minutes).
    ///
    /// This is a local timer and does not consult the provider, which is why
    /// <see cref="RecoverExpiredButPaidTopUpsAsync"/> exists to re-check what it closes.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 30, 60 })]
    public async Task ExpireUnpaidTopUpsAsync()
    {
        var utcNow = DateTime.UtcNow;
        var maxAgeWithoutExpiry = TimeSpan.FromHours(25); // fallback for records without ExpiresAt
        var toExpire = await _walletRepository.GetPendingTopUpsToExpireAsync(utcNow, maxAgeWithoutExpiry);

        foreach (var topUp in toExpire)
        {
            try
            {
                topUp.MarkAsExpired();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark top-up {TopUpId} as expired", topUp.Id);
            }
        }

        if (toExpire.Count > 0)
        {
            await _walletRepository.SaveChangesAsync();
            _logger.LogInformation("Auto-closed {Count} expired unpaid top-ups", toExpire.Count);
        }
    }

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 1)]
    public async Task AlertNegativeWalletsAsync()
    {
        var riskyWallets = await _walletRepository.GetWalletsBelowTopUpThresholdAsync(_options.DefaultCashJobBlockThreshold);
        if (riskyWallets.Count == 0)
            return;

        _logger.LogWarning(
            "Detected {Count} drivers below cash-job threshold {Threshold}. Sample driver IDs: {DriverIds}",
            riskyWallets.Count,
            _options.DefaultCashJobBlockThreshold,
            string.Join(", ", riskyWallets.Take(10).Select(w => w.DriverId)));
    }
}
