using BeeLogistics.Modules.Map.Application;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <inheritdoc cref="ILocationHistoryRetentionService"/>
public sealed class LocationHistoryRetentionService : ILocationHistoryRetentionService
{
    private readonly ILocationRepository _repository;
    private readonly LocationRetentionOptions _options;
    private readonly ILogger<LocationHistoryRetentionService> _logger;

    public LocationHistoryRetentionService(
        ILocationRepository repository,
        IOptions<LocationRetentionOptions> options,
        ILogger<LocationHistoryRetentionService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> PurgeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return 0;

        var cutoff = nowUtc.AddDays(-_options.RetentionDays);
        var deleted = 0;

        // Batched rather than one large DELETE: this is the highest-volume table in the schema, and
        // a single unbounded statement against a long backlog would hold locks while the live
        // ingest path is still writing to it.
        while (deleted < _options.MaxRowsPerRun && !ct.IsCancellationRequested)
        {
            var batchSize = Math.Min(_options.BatchSize, _options.MaxRowsPerRun - deleted);
            var removed = await _repository.DeleteLocationHistoryOlderThanAsync(cutoff, batchSize, ct);

            deleted += removed;

            // A short batch means the backlog is drained; anything left is newer than the cutoff.
            if (removed < batchSize)
                break;
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "[LocationRetention] Deleted {Deleted} location history point(s) older than {Cutoff:yyyy-MM-dd} ({RetentionDays}-day retention).",
                deleted, cutoff, _options.RetentionDays);
        }
        else
        {
            _logger.LogDebug("[LocationRetention] Nothing older than {Cutoff:yyyy-MM-dd} to delete.", cutoff);
        }

        return deleted;
    }
}
