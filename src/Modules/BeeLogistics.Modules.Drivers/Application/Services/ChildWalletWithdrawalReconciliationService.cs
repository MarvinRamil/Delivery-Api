using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Settles child-wallet withdrawals whose <c>transfer.outward.*</c> webhook never arrived
/// (issue #91, phase 3).
///
/// <para>
/// Needed because PESONet stays in flight for up to a banking day, and a webhook that is never
/// delivered leaves the withdrawal stuck in <c>Approved</c> — with the driver's mirror still showing
/// money PayMongo has already sent. They cannot overdraw (the proxy reads PayMongo's balance before
/// every payout), but they are looking at a figure that is simply wrong, and the withdrawal never
/// resolves in their history.
/// </para>
/// <para>
/// One threshold serves both rails. A PESONet transfer polled early just reports <c>pending</c> and
/// is left alone, so there is nothing to gain from tracking which rail each withdrawal took.
/// </para>
/// </summary>
public class ChildWalletWithdrawalReconciliationService
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChildWalletWithdrawalReconciliationService> _logger;

    public ChildWalletWithdrawalReconciliationService(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        TimeProvider clock,
        ILogger<ChildWalletWithdrawalReconciliationService> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!_options.PayMongoWithdrawalsEnabled) return;

        var cutoff = _clock.GetUtcNow().UtcDateTime.AddMinutes(-_options.ChildWithdrawalReconcileAfterMinutes);
        var candidates = await _repository.GetWithdrawalsAwaitingReconciliationAsync(
            cutoff, _options.ChildWithdrawalReconcileBatchSize, ct);

        int settled = 0, failed = 0, stillPending = 0;

        foreach (var withdrawal in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Loaded explicitly: GetWithdrawalsAwaitingReconciliationAsync does not Include the
            // wallet, so reading withdrawal.Wallet here would be null for every row and this
            // service would silently do nothing. Fetching it per row rather than adding an Include
            // keeps the shared query as it is for the platform-path reconciler.
            var wallet = await _repository.GetWalletByDriverIdAsync(withdrawal.DriverId, ct);

            // Only this service's concern. A platform-path withdrawal is reconciled by the existing
            // WithdrawalReconciliationService, which releases the PendingPayout this path never took.
            if (wallet is null || !wallet.UsesPayMongoWallet) continue;
            if (string.IsNullOrWhiteSpace(withdrawal.ProviderDisbursementId)) continue;

            try
            {
                var transfer = await _accounts.GetChildTransferAsync(
                    wallet.PayMongoAccountId!, withdrawal.ProviderDisbursementId!, ct);

                if (transfer is null)
                {
                    // PayMongo has no record of it. Left alone rather than failed: we cannot tell a
                    // transfer that never registered from one we are asking about wrongly, and
                    // marking it failed would tell the driver their money is back when it may not be.
                    _logger.LogWarning(
                        "[PAYMONGO] [WITHDRAWAL] [RECONCILE] Transfer {TransferId} for withdrawal {WithdrawalId} "
                        + "not found; leaving it for manual review",
                        withdrawal.ProviderDisbursementId, withdrawal.Id);
                    continue;
                }

                if (transfer.Succeeded)
                {
                    withdrawal.MarkAsCompleted();
                    // The money left PayMongo, so the mirror follows it down - the same settlement
                    // the webhook would have applied. Amount PLUS fee: PayMongo takes the fee from
                    // the source wallet, so a PHP 50 withdrawal at a PHP 10 fee removes PHP 60.
                    // Debiting only the amount leaves the mirror permanently high by the fee, and
                    // the balance reconciler cannot repair it - it only ever credits, so it would
                    // report that gap as a shortfall on every run instead.
                    wallet.SubtractForRefund(withdrawal.TotalDebitedFromSource);
                    await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);
                    await _repository.UpdateWalletAsync(wallet, ct);
                    settled++;
                }
                else if (transfer.Failed)
                {
                    // No balance change: nothing was deducted locally when the request was made.
                    withdrawal.MarkAsFailed(transfer.ProviderErrorMessage ?? "PayMongo reported the transfer failed.");
                    await _repository.UpdateWithdrawalRequestAsync(withdrawal, ct);
                    failed++;
                }
                else
                {
                    stillPending++;
                }
            }
            catch (Exception ex)
            {
                // One driver's withdrawal must not stop the rest of the batch.
                _logger.LogError(ex,
                    "[PAYMONGO] [WITHDRAWAL] [RECONCILE] Failed to reconcile withdrawal {WithdrawalId}",
                    withdrawal.Id);
            }
        }

        if (settled > 0 || failed > 0 || stillPending > 0)
        {
            _logger.LogInformation(
                "[PAYMONGO] [WITHDRAWAL] [RECONCILE] settled {Settled}, failed {Failed}, still pending {Pending}",
                settled, failed, stillPending);
        }
    }
}
