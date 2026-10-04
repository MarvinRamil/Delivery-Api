using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Settles BeePay ↔ Cash Wallet transfers that PayMongo accepted but had not finished (issue #95).
///
/// <para>
/// Needed because a parent↔child sweep does <b>not</b> always come back settled. Measured on live:
/// a ₱50 <c>Personal → TopUp</c> returned <c>"pending"</c> with <c>fee 0</c>. The transfer handler
/// correctly declines to move the buckets against money that has not landed, which leaves both legs
/// Pending — and without this job they stay that way forever. Worse, a Pending leg makes
/// <see cref="IDriverWalletRepository.HasPendingTransactionsAsync"/> true, which makes the balance
/// reconciler skip that wallet permanently. One unsettled transfer would quietly retire a driver
/// from every automated correction we have.
/// </para>
/// <para>
/// Only the provider's own answer is trusted. A leg is completed when PayMongo says the transfer
/// succeeded and failed when it says it failed; anything else is left alone to be asked again.
/// </para>
/// </summary>
public class WalletTransferReconciliationService
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WalletTransferReconciliationService> _logger;

    public WalletTransferReconciliationService(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        TimeProvider clock,
        ILogger<WalletTransferReconciliationService> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.PayMongoEarningsPushEnabled) return;

        var cutoff = _clock.GetUtcNow().UtcDateTime.AddSeconds(-_options.WalletTransferReconcileAfterSeconds);
        var pending = await _repository.GetPendingWalletTransfersAsync(
            cutoff, _options.WalletTransferReconcileBatchSize, ct);

        int settled = 0, failed = 0, stillPending = 0;

        foreach (var (wallet, outbound, inbound) in pending)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                if (string.IsNullOrWhiteSpace(wallet.PayMongoAccountId))
                    continue;

                var transfer = await _accounts.GetChildTransferAsync(
                    wallet.PayMongoAccountId!, outbound.ProviderPaymentId!, ct);

                if (transfer is null)
                {
                    // Not an absence of the transfer, just an absence of an answer. Leaving it
                    // pending is the only safe reading: the money may well have moved.
                    stillPending++;
                    continue;
                }

                if (transfer.Succeeded)
                {
                    // The buckets move now, for the first time — the handler deliberately left them
                    // untouched. Direction comes from the leg that was debited.
                    if (outbound.Bucket == WalletBucket.Personal)
                        wallet.TransferPersonalToTopUp(outbound.Amount);
                    else
                        wallet.TransferTopUpToPersonal(outbound.Amount);

                    outbound.MarkAsCompleted();
                    inbound.MarkAsCompleted();
                    await _repository.SaveClaimedWalletTransferAsync(wallet, outbound, inbound, ct);

                    settled++;
                    _logger.LogInformation(
                        "[PAYMONGO] [XFER] [RECONCILE] Settled {Amount} {From}->{To} for driver {DriverId} ({TransferId})",
                        outbound.Amount, outbound.Bucket, inbound.Bucket, wallet.DriverId, outbound.ProviderPaymentId);
                }
                else if (transfer.Failed)
                {
                    outbound.MarkAsFailed();
                    inbound.MarkAsFailed();
                    await _repository.UpdateTransactionAsync(outbound, ct);
                    await _repository.UpdateTransactionAsync(inbound, ct);

                    failed++;
                    _logger.LogWarning(
                        "[PAYMONGO] [XFER] [RECONCILE] Transfer {TransferId} for driver {DriverId} failed: {Reason}",
                        outbound.ProviderPaymentId, wallet.DriverId,
                        transfer.ProviderErrorMessage ?? "no reason given");
                }
                else
                {
                    stillPending++;
                }
            }
            catch (Exception ex)
            {
                // One driver's transfer must not stop the sweep.
                _logger.LogError(ex,
                    "[PAYMONGO] [XFER] [RECONCILE] Failed reconciling transfer {TransferId} for driver {DriverId}",
                    outbound.ProviderPaymentId, wallet.DriverId);
            }
        }

        if (pending.Count > 0)
        {
            _logger.LogInformation(
                "[PAYMONGO] [XFER] [RECONCILE] {Total} pending transfers: {Settled} settled, {Failed} failed, {Pending} still pending",
                pending.Count, settled, failed, stillPending);
        }
    }
}
