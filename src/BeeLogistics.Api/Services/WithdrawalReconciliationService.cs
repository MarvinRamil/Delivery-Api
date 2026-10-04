using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Resolves withdrawals the provider accepted but never reported a final status for.
///
/// A withdrawal sits <c>Approved</c> from the moment PayMongo/Xendit accepts the transfer
/// until a <c>transfer.*</c> webhook moves it on. If webhooks are not configured, are
/// misrouted, or are simply dropped, nothing else ever looks at that record: the driver's
/// money stays in PendingPayout indefinitely and no error is raised anywhere. The webhook
/// controller and PAYMENT_GATEWAYS.md have both described this job as the guaranteed path
/// for a while — this is it.
///
/// Completion runs through <see cref="ProcessDisbursementWebhookCommand"/>, the same handler
/// webhooks use, so the ledger entries, outbox events and idempotency checks are identical
/// no matter which path resolves the withdrawal first.
/// </summary>
public class WithdrawalReconciliationService
{
    private readonly IDriverWalletRepository _walletRepository;
    private readonly IPaymentGatewayFactory _gatewayFactory;
    private readonly IMediator _mediator;
    private readonly DriverWalletOptions _options;
    private readonly ILogger<WithdrawalReconciliationService> _logger;

    public WithdrawalReconciliationService(
        IDriverWalletRepository walletRepository,
        IPaymentGatewayFactory gatewayFactory,
        IMediator mediator,
        IOptions<DriverWalletOptions> options,
        ILogger<WithdrawalReconciliationService> logger)
    {
        _walletRepository = walletRepository;
        _gatewayFactory = gatewayFactory;
        _mediator = mediator;
        _options = options.Value;
        _logger = logger;
    }

    /// <remarks>
    /// DisableConcurrentExecution for the same reason as top-up reconciliation: one provider
    /// round-trip per candidate with a pause between each, so a slow provider can overrun the
    /// schedule and overlapping runs would poll the same records twice.
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 30, 120 })]
    public async Task ReconcileWithdrawalsAsync()
    {
        var minAge = TimeSpan.FromMinutes(
            _options.WithdrawalReconciliationMinAgeMinutes > 0 ? _options.WithdrawalReconciliationMinAgeMinutes : 20);
        var batchSize = _options.WithdrawalReconciliationBatchSize > 0 ? _options.WithdrawalReconciliationBatchSize : 50;

        var candidates = await _walletRepository.GetWithdrawalsAwaitingReconciliationAsync(DateTime.UtcNow - minAge, batchSize);

        if (candidates.Count == 0)
        {
            _logger.LogDebug("No withdrawals older than {Minutes} minutes awaiting reconciliation", minAge.TotalMinutes);
            return;
        }

        if (candidates.Count == batchSize)
        {
            _logger.LogInformation(
                "Withdrawal reconciliation checking the {Checked} oldest stuck withdrawals (per-run cap reached)",
                candidates.Count);
        }

        var resolved = 0;
        var errored = 0;

        foreach (var withdrawal in candidates)
        {
            // Guaranteed non-null by the query; belt and braces.
            if (string.IsNullOrWhiteSpace(withdrawal.ProviderDisbursementId))
                continue;

            try
            {
                // Be respectful of the provider's rate limits between calls.
                await Task.Delay(200, CancellationToken.None);

                // Resolve per record so withdrawals created before a gateway switch still
                // reconcile against the provider that actually holds the transfer.
                var gateway = _gatewayFactory.Get(withdrawal.Provider);

                var disbursement = await gateway.GetDisbursementAsync(withdrawal.ProviderDisbursementId);
                if (disbursement == null)
                {
                    _logger.LogDebug(
                        "[WITHDRAWAL] [RECONCILE] Transfer {DisbursementId} not found at {Provider} for withdrawal {WithdrawalId}",
                        withdrawal.ProviderDisbursementId, withdrawal.Provider, withdrawal.Id);
                    continue;
                }

                // Still in flight — the rail has not settled it. Leave it for the next run.
                if (disbursement.Status == GatewayDisbursementStatus.Pending)
                    continue;

                var status = disbursement.Status == GatewayDisbursementStatus.Completed ? "SUCCEEDED" : "FAILED";

                var result = await _mediator.Send(new ProcessDisbursementWebhookCommand(
                    withdrawal.Provider,
                    withdrawal.ProviderDisbursementId,
                    Event: "reconciliation",
                    Status: status,
                    FailureReason: disbursement.FailureCode));

                if (result.IsSuccess)
                {
                    resolved++;
                    _logger.LogInformation(
                        "[WITHDRAWAL] [RECONCILE] Withdrawal {WithdrawalId} resolved as {Status} (provider said '{RawStatus}') — the {Provider} transfer webhook never arrived",
                        withdrawal.Id, status, disbursement.RawStatus, withdrawal.Provider);
                }
                else
                {
                    // The provider has reached a terminal state and we could not apply it, so
                    // the driver's money stays held. That is a warning, not a debug line.
                    errored++;
                    _logger.LogWarning(
                        "[WITHDRAWAL] [RECONCILE] Withdrawal {WithdrawalId} is {RawStatus} at {Provider} but could not be applied: {Error}",
                        withdrawal.Id, disbursement.RawStatus, withdrawal.Provider, result.Error);
                }
            }
            catch (Exception ex)
            {
                // One bad record must not stop the batch.
                errored++;
                _logger.LogError(ex,
                    "[WITHDRAWAL] [RECONCILE] Failed to reconcile withdrawal {WithdrawalId} ({Provider} transfer {DisbursementId})",
                    withdrawal.Id, withdrawal.Provider, withdrawal.ProviderDisbursementId);
            }
        }

        if (resolved > 0)
        {
            _logger.LogWarning(
                "Withdrawal reconciliation resolved {Count} stuck withdrawals. Transfer webhooks are being missed — check the PayMongo webhook registration.",
                resolved);
        }

        if (errored > 0)
            _logger.LogWarning("Withdrawal reconciliation hit {ErrorCount} errors across {Total} records", errored, candidates.Count);

        if (resolved == 0 && errored == 0)
            _logger.LogDebug("Withdrawal reconciliation completed: {Count} checked, all still in flight", candidates.Count);

        await ResolveUnconfirmedReservationsAsync(minAge, batchSize);
    }

    /// <summary>
    /// Second pass: reservations that were debited but never got a provider id back, because
    /// the create call timed out or died mid-flight.
    ///
    /// These cannot be refunded on sight — the transfer may well exist. The reference id we
    /// set on create (<c>WD-{withdrawalId}</c>) is the only way to ask the provider what
    /// actually happened, so each is looked up by reference and only released when the
    /// provider positively has no record of it.
    /// </summary>
    private async Task ResolveUnconfirmedReservationsAsync(TimeSpan minAge, int batchSize)
    {
        var reservations = await _walletRepository.GetUnconfirmedReservationsAsync(DateTime.UtcNow - minAge, batchSize);
        if (reservations.Count == 0)
            return;

        _logger.LogWarning(
            "Found {Count} withdrawal reservations with no provider id — funds are held for payouts whose fate is unknown",
            reservations.Count);

        foreach (var withdrawal in reservations)
        {
            try
            {
                await Task.Delay(200, CancellationToken.None);

                var gateway = _gatewayFactory.Get(withdrawal.Provider);
                var referenceId = $"WD-{withdrawal.Id}";

                GatewayDisbursement? found;
                try
                {
                    found = await gateway.FindDisbursementByReferenceAsync(referenceId);
                }
                catch (NotSupportedException)
                {
                    // Xendit cannot answer this. Leave it held rather than guess; an operator
                    // has to settle it against the provider dashboard.
                    _logger.LogWarning(
                        "[WITHDRAWAL] [RECONCILE] {Provider} cannot look up {Reference} by reference; withdrawal {WithdrawalId} needs manual review",
                        withdrawal.Provider, referenceId, withdrawal.Id);
                    continue;
                }

                if (found == null)
                {
                    // The provider positively has no such transfer, so the money never left.
                    // This is the only case where releasing the hold is safe.
                    await _mediator.Send(new ReleaseWithdrawalReservationCommand(
                        withdrawal.Id, "Provider has no record of this transfer; funds released."));

                    _logger.LogWarning(
                        "[WITHDRAWAL] [RECONCILE] Released reservation {WithdrawalId} — {Provider} has no transfer for {Reference}",
                        withdrawal.Id, withdrawal.Provider, referenceId);
                    continue;
                }

                // The transfer does exist. Adopt its id so the normal Approved-path
                // reconciliation above can track it from here on.
                await _mediator.Send(new AdoptWithdrawalDisbursementCommand(
                    withdrawal.Id, withdrawal.Provider, found.ProviderDisbursementId, found.Amount, found.RawStatus, found.FailureCode));

                _logger.LogWarning(
                    "[WITHDRAWAL] [RECONCILE] Reservation {WithdrawalId} DID reach {Provider} as {DisbursementId} ({RawStatus}) — the create response was lost, not the transfer",
                    withdrawal.Id, withdrawal.Provider, found.ProviderDisbursementId, found.RawStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WITHDRAWAL] [RECONCILE] Failed to resolve reservation {WithdrawalId} for driver {DriverId}",
                    withdrawal.Id, withdrawal.DriverId);
            }
        }
    }
}
