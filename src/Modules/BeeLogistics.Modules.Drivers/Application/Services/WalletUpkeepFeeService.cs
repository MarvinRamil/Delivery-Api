using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Charges the monthly PayMongo wallet upkeep to each driver who has one (issue #91).
///
/// <para>
/// Lives in the module rather than the API host so it can be unit tested - the test project
/// references the modules but not the host, which is why the reconciliation services alongside it
/// have no coverage. Scheduling stays in Program.cs.
/// </para>
///
/// <para>
/// The fee is drawn against <c>TopUpBalance</c>, not the driver's PayMongo wallet. That bucket is
/// the float they funded for operating costs and is the only one allowed to go negative; their
/// PayMongo wallet holds earnings, which are theirs. Sweeping it out of PayMongo would also mean
/// one API call per driver per month, and would simply fail whenever a wallet was empty.
/// </para>
/// <para>
/// Charging below zero is deliberate. The alternative - skipping drivers who cannot pay - silently
/// forgives the fee for exactly the drivers costing us money. The existing cash-job block at
/// <c>DefaultCashJobBlockThreshold</c> already forces a top-up before they can earn again.
/// </para>
/// </summary>
public class WalletUpkeepFeeService
{
    private const int PageSize = 200;

    private readonly IDriverWalletRepository _repository;
    private readonly DriverWalletOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WalletUpkeepFeeService> _logger;

    public WalletUpkeepFeeService(
        IDriverWalletRepository repository,
        IOptions<DriverWalletOptions> options,
        TimeProvider clock,
        ILogger<WalletUpkeepFeeService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task ChargeMonthlyUpkeepAsync(CancellationToken ct = default)
    {
        if (!_options.AccountFeesEnabled)
            return;

        if (_options.MonthlyWalletFeeAmount <= 0)
        {
            _logger.LogWarning(
                "[WALLET] [UPKEEP] MonthlyWalletFeeAmount is {Amount}; nothing to charge",
                _options.MonthlyWalletFeeAmount);
            return;
        }

        // One key per wallet per calendar month. The filtered unique index on
        // (WalletId, Type, ProviderPaymentId) makes a second attempt in the same month a no-op, so
        // the job is safe to re-run, retry, or overlap with itself - none of which can double-charge.
        var now = _clock.GetUtcNow();
        var period = $"{now.Year:D4}-{now.Month:D2}";
        var negativeLimit = _options.DefaultTopUpNegativeLimit;

        int charged = 0, skipped = 0, failed = 0;
        Guid? cursor = null;

        while (!ct.IsCancellationRequested)
        {
            var page = await _repository.GetActivatedPayMongoWalletsAsync(cursor, PageSize, ct);
            if (page.Count == 0) break;

            foreach (var wallet in page)
            {
                cursor = wallet.Id;
                var key = $"acctfee-upkeep-{period}";

                try
                {
                    if (await _repository.HasTransactionForProviderPaymentAsync(
                            wallet.Id, key, WalletTransactionType.AccountFee, ct))
                    {
                        skipped++;
                        continue;
                    }

                    wallet.ChargeAccountFee(_options.MonthlyWalletFeeAmount,
                        wallet.ResolveTopUpNegativeLimit(negativeLimit));

                    var fee = new WalletTransaction(
                        wallet.Id,
                        WalletTransactionType.AccountFee,
                        WalletBucket.TopUp,
                        // Positive: the type carries the direction, not the sign. See the
                        // KYC fee in PayMongoOnboardingHandlers for the same reasoning.
                        _options.MonthlyWalletFeeAmount,
                        WalletTransactionStatus.Completed,
                        $"Wallet upkeep {period}",
                        providerPaymentId: key);

                    await _repository.ApplyTransactionAsync(wallet, fee, ct);
                    charged++;
                }
                catch (InvalidOperationException ex)
                {
                    // Past the negative floor. Not charged, and deliberately not retried into a
                    // deeper hole - the driver has to top up, which the cash-job block enforces.
                    failed++;
                    _logger.LogWarning(
                        "[WALLET] [UPKEEP] Skipped driver {DriverId} for {Period}: {Reason}",
                        wallet.DriverId, period, ex.Message);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex,
                        "[WALLET] [UPKEEP] Failed to charge driver {DriverId} for {Period}",
                        wallet.DriverId, period);
                }
            }

            if (page.Count < PageSize) break;
        }

        _logger.LogInformation(
            "[WALLET] [UPKEEP] {Period}: charged {Charged}, already charged {Skipped}, not charged {Failed}",
            period, charged, skipped, failed);
    }
}
