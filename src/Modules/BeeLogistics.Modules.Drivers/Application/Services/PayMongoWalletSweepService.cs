using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Rollback for phase 2: pulls a driver's balance out of their PayMongo wallet and back to the
/// platform, then unlinks them so they resume the original local-balance path.
///
/// <para>
/// This exists because turning off <c>PayMongoEarningsPushEnabled</c> is <b>not</b> a rollback on
/// its own. Once earnings have landed in child wallets, that money physically sits at PayMongo;
/// flipping the flag would simply stop new earnings arriving and strand everything already there,
/// where our ledger cannot reach it. Written and tested before the flag is ever enabled, because an
/// untested rollback is not a rollback.
/// </para>
/// <para>
/// The sweep is in-network (platform ↔ child) and therefore free, so recovering costs nothing
/// beyond the ₱30 already spent opening the account.
/// </para>
/// </summary>
public class PayMongoWalletSweepService
{
    private readonly IDriverWalletRepository _repository;
    private readonly IPayMongoAccountsClient _accounts;
    private readonly ILogger<PayMongoWalletSweepService> _logger;

    public PayMongoWalletSweepService(
        IDriverWalletRepository repository,
        IPayMongoAccountsClient accounts,
        ILogger<PayMongoWalletSweepService> logger)
    {
        _repository = repository;
        _accounts = accounts;
        _logger = logger;
    }

    public sealed record SweepResult(bool Swept, decimal Amount, string? Reason);

    /// <summary>
    /// Sweeps one driver's wallet back to the platform.
    /// </summary>
    /// <param name="unlink">
    /// When true the driver is returned to the local-balance path. Left false to recover the money
    /// without giving up the account, e.g. while pausing rather than abandoning the migration.
    /// </param>
    public async Task<SweepResult> SweepAsync(Guid driverId, bool unlink, CancellationToken ct = default)
    {
        var wallet = await _repository.GetWalletByDriverIdAsync(driverId, ct);
        if (wallet is null)
            return new SweepResult(false, 0m, "Driver wallet not found.");

        if (!wallet.UsesPayMongoWallet)
            return new SweepResult(false, 0m, "Driver is not on the PayMongo wallet path.");

        // PayMongo's balance is authoritative here, not our mirror. Sweeping the mirror's figure
        // would fail if it had drifted high, and would leave money behind if it had drifted low.
        var remote = await _accounts.GetWalletAsync(wallet.PayMongoAccountId!, includeBalance: true, ct: ct);
        var available = remote?.AvailableBalance ?? 0m;

        if (available <= 0m)
        {
            if (unlink) await UnlinkAsync(wallet, ct);
            return new SweepResult(false, 0m, "Nothing to sweep.");
        }

        var reference = $"sweep-{wallet.Id:N}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var transfer = await _accounts.SweepFromChildAsync(
            wallet.PayMongoAccountId!,
            wallet.PayMongoAccountNumber!,
            wallet.AccountHolderName ?? "Driver",
            available,
            reference,
            "Wallet migration reversal",
            ct);

        if (transfer.Failed)
        {
            // Deliberately leaves the link intact: unlinking now would hide a wallet that still
            // holds the driver's money behind a flag nobody is looking at any more.
            _logger.LogError(
                "[PAYMONGO] [SWEEP] Failed to recover {Amount} from driver {DriverId}: {Reason}",
                available, driverId, transfer.ProviderErrorMessage ?? "no reason given");
            return new SweepResult(false, available,
                transfer.ProviderErrorMessage ?? "PayMongo rejected the sweep.");
        }

        _logger.LogInformation(
            "[PAYMONGO] [SWEEP] Recovered {Amount} from driver {DriverId} ({TransferId})",
            available, driverId, transfer.TransferId);

        if (unlink) await UnlinkAsync(wallet, ct);

        return new SweepResult(true, available, null);
    }

    /// <summary>
    /// Returns the driver to the local-balance path.
    /// <para>
    /// The local mirror is left exactly as it is: it already reflects the earnings that were pushed
    /// to PayMongo, and the swept funds have just been returned to the platform wallet backing it.
    /// Zeroing or re-crediting here would double-count.
    /// </para>
    /// </summary>
    private async Task UnlinkAsync(DriverWallet wallet, CancellationToken ct)
    {
        wallet.UnlinkPayMongoAccount();
        await _repository.UpdateWalletAsync(wallet, ct);
        _logger.LogInformation(
            "[PAYMONGO] [SWEEP] Driver {DriverId} returned to the local balance path", wallet.DriverId);
    }
}
