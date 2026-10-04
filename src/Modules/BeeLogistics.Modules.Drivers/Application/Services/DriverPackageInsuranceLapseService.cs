using BeeLogistics.Modules.Drivers.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Drivers.Application.Services;

/// <summary>
/// Flips an <c>Active</c> package-insurance policy to <c>Lapsed</c> once its coverage year has
/// run out and not been renewed (issue #104). Lives in the module rather than the API host so it
/// can be unit tested, same reasoning as <see cref="WalletUpkeepFeeService"/> — scheduling stays
/// in Program.cs.
/// <para>
/// Status-only: this job never charges anyone. Premium collection is driver-initiated via QR
/// (<c>CreateInsurancePremiumQrCommand</c>), exactly like cashbond, never auto-swept — and it
/// sends no notification. T-30/T-7/T-1 expiry warnings and any booking-eligibility gate on a
/// lapsed policy are deliberately out of scope here, deferred alongside the driver-app expiry-UX
/// work (bee-driver#38).
/// </para>
/// </summary>
public class DriverPackageInsuranceLapseService
{
    private const int PageSize = 200;

    private readonly IDriverWalletRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<DriverPackageInsuranceLapseService> _logger;

    public DriverPackageInsuranceLapseService(
        IDriverWalletRepository repository,
        TimeProvider clock,
        ILogger<DriverPackageInsuranceLapseService> logger)
    {
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public async Task EnforceExpiryAsync(CancellationToken ct = default)
    {
        var utcNow = _clock.GetUtcNow().UtcDateTime;

        int lapsed = 0, scanned = 0;
        Guid? cursor = null;

        while (!ct.IsCancellationRequested)
        {
            var page = await _repository.GetActiveInsurancePoliciesAsync(cursor, PageSize, ct);
            if (page.Count == 0) break;

            foreach (var policy in page)
            {
                cursor = policy.Id;
                scanned++;

                if (policy.CoverageEndDate is null || policy.CoverageEndDate >= utcNow)
                    continue;

                policy.MarkLapsed();
                await _repository.SaveInsurancePolicyAsync(policy, ct);
                lapsed++;
            }

            if (page.Count < PageSize) break;
        }

        _logger.LogInformation(
            "[DRIVERS] [PACKAGE-INSURANCE] Lapse sweep: scanned {Scanned}, lapsed {Lapsed}",
            scanned, lapsed);
    }
}
