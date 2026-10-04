using BeeLogistics.Modules.Map.Application.Events;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Modules.Map.Infrastructure;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using BeeLogistics.Modules.Map.Presentation.Hubs;
using BeeLogistics.Modules.Identity.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Application.Consumers;

/// <summary>
/// MassTransit consumer for driver location updates from RabbitMQ
/// This is the processing layer that:
/// 1. Updates Redis for real-time queries
/// 2. Batches writes to PostgreSQL for history
/// 3. Publishes SignalR events for customer visibility
/// </summary>
public sealed class DriverLocationUpdatedConsumer : IConsumer<DriverLocationUpdatedEvent>
{
    private readonly IRedisLocationCache _redisCache;
    private readonly IDriverGeoIndex _geoIndex;
    private readonly ILocationHistoryBatchWriter _historyWriter;
    private readonly ILocationRepository _repository;
    private readonly IHubContext<LocationHub> _hubContext;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<DriverLocationUpdatedConsumer> _logger;

    // Only sync coordinates back to the Identity ApplicationUser row when the
    // driver has moved at least this far; matching reads Redis/H3, the Identity
    // columns are kept fresh enough for admin/dashboard views only.
    private const double IdentitySyncMinimumDeltaDegrees = 0.002; // ~200 m

    public DriverLocationUpdatedConsumer(
        IRedisLocationCache redisCache,
        IDriverGeoIndex geoIndex,
        ILocationHistoryBatchWriter historyWriter,
        ILocationRepository repository,
        IHubContext<LocationHub> hubContext,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<DriverLocationUpdatedConsumer> logger)
    {
        _redisCache = redisCache;
        _geoIndex = geoIndex;
        _historyWriter = historyWriter;
        _repository = repository;
        _hubContext = hubContext;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<DriverLocationUpdatedEvent> context)
    {
        var locationEvent = context.Message;
        var ct = context.CancellationToken;

        try
        {
            _logger.LogInformation("[RabbitMQ] Consuming location event for driver {DriverId} at ({Latitude}, {Longitude}) via {Source}",
                locationEvent.DriverId, locationEvent.Latitude, locationEvent.Longitude, locationEvent.Source);

            // Historical points are backfill, not news: they go to history and stop there.
            //
            // Everything below this line describes where the driver *is* - the Redis hot cache, the
            // H3 cell index that offer matching reads, the DriverLocation snapshot, the
            // ApplicationUser coordinates, and the SignalR push to whoever is watching. Replaying a
            // buffered backlog through all of that would rewind the driver's current position to
            // where they were when the app went quiet, hand the matcher a stale cell to offer
            // bookings against, and walk the tracking customer's marker back through the entire old
            // trail one point at a time.
            //
            // The live path re-establishes current state on the next real fix, which arrives as
            // soon as the app foregrounds and MQTT reconnects.
            if (locationEvent.IsHistorical)
            {
                await _historyWriter.EnqueueAsync(ToHistoryEntry(locationEvent));

                _logger.LogInformation(
                    "[RabbitMQ] Backfilled historical location for driver {DriverId} recorded {Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC via {Source} (history only)",
                    locationEvent.DriverId, locationEvent.Timestamp, locationEvent.Source);
                return;
            }

            // Check for stale data (older than 2 minutes = likely buffered/historical).
            // This guard was always keyed on IsHistorical; it was only ever ineffective because the
            // HTTP path stamped every buffered point with the receive time, so nothing looked old.
            var dataAge = DateTime.UtcNow - locationEvent.Timestamp;
            if (dataAge > TimeSpan.FromMinutes(2))
            {
                _logger.LogWarning("Skipping stale location update for driver {DriverId} (age: {Age}s)",
                    locationEvent.DriverId, dataAge.TotalSeconds);
                return;
            }

            // 1. Update Redis (hot storage) - Fire and forget for speed (or await if we want consistency)
            // We await here to ensure Redis is updated before we consider the message consumed
            await _redisCache.SetDriverLocationAsync(
                locationEvent.DriverId,
                locationEvent.Latitude,
                locationEvent.Longitude,
                locationEvent.Speed,
                locationEvent.Heading,
                ct);

            // Update the H3 cell index used for nearest-driver matching
            await _geoIndex.IndexDriverAsync(
                locationEvent.DriverId,
                locationEvent.Latitude,
                locationEvent.Longitude,
                ct);

            // 2. Update SQL Snapshot (DriverLocation table)
            // This replaces the synchronous write that was in the API Handler
            var location = await _repository.GetCurrentLocationByDriverIdAsync(locationEvent.DriverId, ct);
            
            if (location == null)
            {
                location = new DriverLocation(
                    locationEvent.DriverId,
                    locationEvent.Latitude,
                    locationEvent.Longitude,
                    locationEvent.Speed,
                    locationEvent.Heading,
                    locationEvent.DeviceId
                );
                _repository.Add(location);
            }
            else
            {
                location.UpdateLocation(
                    locationEvent.Latitude, 
                    locationEvent.Longitude, 
                    locationEvent.Speed, 
                    locationEvent.Heading
                );
                if (!string.IsNullOrEmpty(locationEvent.DeviceId))
                {
                    location.DeviceId = locationEvent.DeviceId;
                }
            }
            
            // Save changes to DB
            await _repository.SaveChangesAsync(ct);

            // 3. Queue for batch write to SQL History (cold storage)
            await _historyWriter.EnqueueAsync(ToHistoryEntry(locationEvent));

            // 4. Sync driver location to Identity.ApplicationUser (used by DriverAvailabilityService for offer matching)
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var driver = await userManager.FindByIdAsync(locationEvent.DriverId.ToString());
                if (driver != null)
                {
                    var movedFar = !driver.CurrentLatitude.HasValue || !driver.CurrentLongitude.HasValue ||
                        Math.Abs((double)(driver.CurrentLatitude.Value - locationEvent.Latitude)) > IdentitySyncMinimumDeltaDegrees ||
                        Math.Abs((double)(driver.CurrentLongitude.Value - locationEvent.Longitude)) > IdentitySyncMinimumDeltaDegrees;
                    if (movedFar)
                    {
                        driver.CurrentLatitude = locationEvent.Latitude;
                        driver.CurrentLongitude = locationEvent.Longitude;
                        await userManager.UpdateAsync(driver);
                        _logger.LogDebug("Synced driver {DriverId} location to ApplicationUser (moved beyond threshold)", locationEvent.DriverId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync location to ApplicationUser for driver {DriverId}, will only broadcast to driver-specific group", locationEvent.DriverId);
            }

            // Payload matches the frontend expectation
            var payload = new 
            {
                driverId = locationEvent.DriverId,
                latitude = locationEvent.Latitude,
                longitude = locationEvent.Longitude,
                speed = locationEvent.Speed,
                heading = locationEvent.Heading,
                timestamp = locationEvent.Timestamp
            };

            // Broadcast to the driver-specific group (for customers tracking this driver).
            // Use ToString() to ensure consistent Guid formatting (standard format with hyphens)
            var driverGroup = $"driver-{locationEvent.DriverId.ToString()}";
            await _hubContext.Clients.Group(driverGroup).SendAsync("ReceiveLocationUpdate", payload, ct);
            _logger.LogInformation("[RabbitMQ→SignalR] Broadcast location for driver {DriverId} to group {DriverGroup}",
                locationEvent.DriverId, driverGroup);

            _logger.LogInformation("[RabbitMQ] Processed location for driver {DriverId} (Redis + DB + SignalR done)", locationEvent.DriverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process location update for driver {DriverId}", locationEvent.DriverId);
            // We might want to throw here to trigger MassTransit retry,
            // but for location updates, it's often better to just drop it than clog the queue
        }
    }

    private static LocationHistoryEntry ToHistoryEntry(DriverLocationUpdatedEvent locationEvent) => new()
    {
        DriverId = locationEvent.DriverId,
        Latitude = locationEvent.Latitude,
        Longitude = locationEvent.Longitude,
        Speed = locationEvent.Speed,
        Heading = locationEvent.Heading,
        Accuracy = locationEvent.Accuracy,
        Timestamp = locationEvent.Timestamp,
        DeviceId = locationEvent.DeviceId,
        Source = locationEvent.Source,
        IsHistorical = locationEvent.IsHistorical
    };
}

/// <summary>
/// Batch writer for location history to minimize database writes
/// Uses System.Threading.Timer for time-based flushing
/// </summary>
public interface ILocationHistoryBatchWriter
{
    Task EnqueueAsync(LocationHistoryEntry entry);
    Task FlushAsync();
}

public sealed class LocationHistoryBatchWriter : ILocationHistoryBatchWriter, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LocationHistoryBatchWriter> _logger;
    private readonly List<LocationHistoryEntry> _buffer = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _flushTimer;

    private const int BatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    public LocationHistoryBatchWriter(
        IServiceScopeFactory scopeFactory,
        ILogger<LocationHistoryBatchWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        // Setup periodic flush timer
        _flushTimer = new Timer(
            async _ => await FlushAsync(),
            null,
            FlushInterval,
            FlushInterval);
    }

    public async Task EnqueueAsync(LocationHistoryEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            _buffer.Add(entry);

            // Flush if batch size reached
            if (_buffer.Count >= BatchSize)
            {
                await FlushInternalAsync();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task FlushAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await FlushInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FlushInternalAsync()
    {
        if (_buffer.Count == 0)
            return;

        var entriesToWrite = _buffer.ToList();
        _buffer.Clear();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MapDbContext>();

            // Bulk insert
            await dbContext.LocationHistory.AddRangeAsync(entriesToWrite.Select(ToEntity));
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("Flushed {Count} location history entries to database", entriesToWrite.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk flush of {Count} location history entries failed; retrying individually", entriesToWrite.Count);

            // One bad row used to cost the whole buffer - up to 100 perfectly good points thrown
            // away because a single entry could not be written. Retry individually so the blast
            // radius is the offending row and nothing else. Still no re-buffering on failure: that
            // is deliberate, an unbounded retry queue for GPS breadcrumbs is a memory leak.
            var written = await WriteIndividuallyAsync(entriesToWrite);
            _logger.LogWarning("Individual retry recovered {Written}/{Total} location history entries", written, entriesToWrite.Count);
        }
    }

    private async Task<int> WriteIndividuallyAsync(IReadOnlyList<LocationHistoryEntry> entries)
    {
        var written = 0;

        foreach (var entry in entries)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MapDbContext>();

                dbContext.LocationHistory.Add(ToEntity(entry));
                await dbContext.SaveChangesAsync();
                written++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dropped location history entry for driver {DriverId} at {Timestamp:O}", entry.DriverId, entry.Timestamp);
            }
        }

        return written;
    }

    private static DriverLocationHistory ToEntity(LocationHistoryEntry e) => new(
        e.DriverId,
        e.Latitude,
        e.Longitude,
        e.Speed,
        e.Heading,
        e.DeviceId,
        e.Timestamp,
        e.Accuracy,
        e.Source,
        e.IsHistorical
    );

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _lock?.Dispose();
    }
}

public sealed record LocationHistoryEntry
{
    public required Guid DriverId { get; init; }
    public required decimal Latitude { get; init; }
    public required decimal Longitude { get; init; }
    public decimal? Speed { get; init; }
    public decimal? Heading { get; init; }
    public decimal? Accuracy { get; init; }
    public DateTime Timestamp { get; init; }
    public string? DeviceId { get; init; }
    public LocationSource Source { get; init; }
    public bool IsHistorical { get; init; }
}
