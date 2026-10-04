using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Brings each activated driver's local mirror back in line with the balance PayMongo actually
/// holds (issue #96).
///
/// <para>
/// Needed because <b>transaction events on a child account are never delivered to the parent's
/// webhook</b>. PayMongo sends onboarding events to the parent, but a <c>qr.paid</c> on a driver's
/// own wallet is emitted on that child — so unless a webhook is registered on every child, a
/// driver's top-up lands at PayMongo and our mirror never hears about it. That is not a hypothetical:
/// it stranded a real ₱50 top-up, and because the event was never emitted it will never be
/// redelivered either.
/// </para>
/// <para>
/// <b>Phase 2 onward only.</b> Before the earnings push is enabled the mirror is not a mirror at
/// all — earnings live locally and the child wallet holds only QR top-ups — so the two balances are
/// unrelated and their difference means nothing.
/// </para>
/// <para>
/// Polling is the better instrument for this than webhooks. It catches <i>any</i> divergence,
/// including events we never thought to subscribe to, and needs neither per-child registration nor
/// the per-child signing secret each of those webhooks would carry.
/// </para>
/// </summary>
public class PayMongoBalanceReconciliationService
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly DriverWalletOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PayMongoBalanceReconciliationService> _logger;

    public PayMongoBalanceReconciliationService(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        IOptions<DriverWalletOptions> options,
        TimeProvider clock,
        ILogger<PayMongoBalanceReconciliationService> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        // Requires the earnings push, not just onboarding. Until phase 2 is on, a driver's earnings
        // are credited locally by AddEarning and never sent to PayMongo, so Balance and the child
        // wallet are two unrelated pools: the mirror holds what we owe them, the wallet holds only
        // what they topped up by QR. Their difference is not drift and closing it is not a
        // correction -- on a real wallet it read as a PHP 1,160 shortfall when nothing was wrong.
        //
        // The comparison this job makes is only meaningful once every credit to the mirror
        // corresponds to money actually moved to PayMongo, which is exactly what
        // PayMongoEarningsPushEnabled establishes.
        if (!_options.PayMongoEarningsPushEnabled)
        {
            _logger.LogInformation(
                "[PAYMONGO] [RECONCILE] Skipped: DriverWallet:PayMongoEarningsPushEnabled is false, so "
                + "the local balance is not a mirror of PayMongo and the two cannot be compared.");
            return;
        }

        if (!_options.PayMongoBalanceReconciliationEnabled)
        {
            // Says so out loud rather than returning silently. A disabled sweep and a broken one
            // produce identical evidence otherwise — no log lines, and a Hangfire run that reports
            // success — which is exactly how this was first mistaken for a job that never ran.
            _logger.LogInformation(
                "[PAYMONGO] [RECONCILE] Disabled: DriverWallet:PayMongoBalanceReconciliationEnabled is false. "
                + "Set DriverWallet__PayMongoBalanceReconciliationEnabled=true and restart "
                + "(options bind once at startup, and the Vault overlay is applied after environment variables).");
            return;
        }

        Guid? after = null;
        int credited = 0, skipped = 0, shortfalls = 0, examined = 0;

        while (examined < _options.BalanceReconcileMaxWalletsPerRun)
        {
            if (ct.IsCancellationRequested) break;

            var take = Math.Min(
                _options.BalanceReconcilePageSize,
                _options.BalanceReconcileMaxWalletsPerRun - examined);

            var page = await _repository.GetActivatedPayMongoWalletsAsync(after, take, ct);
            if (page.Count == 0) break;

            foreach (var wallet in page)
            {
                if (ct.IsCancellationRequested) break;
                examined++;
                after = wallet.Id;

                try
                {
                    if (await ReconcileWalletAsync(wallet, ct) is { } outcome)
                    {
                        if (outcome == Outcome.Credited) credited++;
                        else if (outcome == Outcome.Shortfall) shortfalls++;
                        else skipped++;
                    }
                }
                catch (Exception ex)
                {
                    // One driver's wallet must never stop the sweep: a single account in a bad
                    // state at PayMongo would otherwise strand every wallet ordered after it.
                    _logger.LogError(ex,
                        "[PAYMONGO] [RECONCILE] Failed reconciling driver {DriverId}", wallet.DriverId);
                }
            }

            if (page.Count < take) break;
        }

        _logger.LogInformation(
            "[PAYMONGO] [RECONCILE] Examined {Examined} wallets: {Credited} credited, "
            + "{Skipped} skipped as busy, {Shortfalls} short of the mirror",
            examined, credited, skipped, shortfalls);
    }

    private enum Outcome { Credited, Skipped, Shortfall, Level }

    private async Task<Outcome?> ReconcileWalletAsync(DriverWallet wallet, CancellationToken ct)
    {
        if (!wallet.UsesPayMongoWallet || string.IsNullOrWhiteSpace(wallet.PayMongoAccountId))
            return null;

        // A withdrawal debits Balance and parks it in PendingPayout *before* PayMongo sends, so a
        // wallet mid-withdrawal legitimately reads low locally and high at PayMongo. Crediting that
        // gap would hand the driver money that is already on its way out.
        if (wallet.PendingPayout != 0)
            return Outcome.Skipped;

        // Same reasoning for a claim-before-send row: written before the provider call, so the two
        // views disagree until it resolves. That gap closes itself.
        if (await _repository.HasPendingTransactionsAsync(wallet.Id, ct))
            return Outcome.Skipped;

        var remote = await _accounts.GetWalletAsync(
            wallet.PayMongoAccountId!, includeAccount: false, includeBalance: true, ct: ct);

        if (remote?.AvailableBalance is not { } available)
        {
            _logger.LogWarning(
                "[PAYMONGO] [RECONCILE] No balance returned for driver {DriverId}; leaving the mirror alone",
                wallet.DriverId);
            return null;
        }

        var drift = available - wallet.Balance;
        if (drift == 0) return Outcome.Level;

        if (drift < 0)
        {
            // PayMongo holds LESS than we think. Money left the wallet without us recording it —
            // a dashboard-initiated transfer, or something we have not modelled. Deliberately not
            // corrected: debiting on a reading we cannot explain risks taking the money twice, and
            // an unexplained shortfall is a question for a human, not a number to overwrite.
            _logger.LogCritical(
                "[PAYMONGO] [RECONCILE] [SHORTFALL] Driver {DriverId} mirror shows {Mirror} but "
                + "PayMongo holds {Available} (short by {Short}). Not auto-correcting.",
                wallet.DriverId, wallet.Balance, available, -drift);
            return Outcome.Shortfall;
        }

        // Keyed on the wallet and the drift so a redelivery or an overlapping run collides on the
        // unique index rather than crediting twice. Includes the day so a genuinely repeated drift
        // of the same size on a later date is still credited.
        var key = $"recon-{wallet.PayMongoAccountId}-{_clock.GetUtcNow().UtcDateTime:yyyyMMdd}-{drift:0.00}";

        if (await _repository.HasTransactionForProviderPaymentAsync(
                wallet.Id, key, WalletTransactionType.TopUp, ct))
            return Outcome.Skipped;

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            // Personal: the child wallet is the withdrawable side. Same bucket the qr.paid handler
            // credits, so a payment recovered here and one caught live are indistinguishable.
            WalletBucket.Personal,
            drift,
            WalletTransactionStatus.Completed,
            "Top-up",
            providerPaymentId: key);

        wallet.AddBeeWalletTopUp(drift);
        await _repository.ApplyTransactionAsync(wallet, transaction, ct);

        _logger.LogInformation(
            "[PAYMONGO] [RECONCILE] Credited {Drift} to driver {DriverId}; mirror now matches PayMongo at {Available}",
            drift, wallet.DriverId, available);

        return Outcome.Credited;
    }
}
