using BeeLogistics.Modules.Map.Application.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Hangfire wrapper for location trail retention (GitLab #39).
///
/// Thin on purpose: the behaviour lives in <see cref="ILocationHistoryRetentionService"/> in the Map
/// module, where the test project can reach it. Only the scheduling concern belongs here, because
/// the Map module does not reference Hangfire. Same split as PaymentReconciliationService.
/// </summary>
public class LocationHistoryRetentionJob
{
    private readonly ILocationHistoryRetentionService _retentionService;
    private readonly ILogger<LocationHistoryRetentionJob> _logger;

    public LocationHistoryRetentionJob(
        ILocationHistoryRetentionService retentionService,
        ILogger<LocationHistoryRetentionJob> logger)
    {
        _retentionService = retentionService;
        _logger = logger;
    }

    // A long-running delete must not overlap itself: two runs would compete for locks on the same
    // rows of the busiest table in the schema. Matches the booking and payment jobs.
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 1)]
    public async Task PurgeAsync()
    {
        var deleted = await _retentionService.PurgeAsync(DateTime.UtcNow);

        if (deleted > 0)
            _logger.LogInformation("[LocationRetention] Purge run removed {Deleted} trail point(s).", deleted);
    }
}
