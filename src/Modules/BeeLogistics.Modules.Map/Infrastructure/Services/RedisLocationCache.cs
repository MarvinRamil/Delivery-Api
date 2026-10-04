using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Redis-based geospatial cache for driver locations
/// Uses Redis GEO commands for ultra-fast proximity queries
/// </summary>
public interface IRedisLocationCache
{
    Task SetDriverLocationAsync(Guid driverId, decimal latitude, decimal longitude, decimal? speed, decimal? heading, CancellationToken ct = default);
    Task<(decimal Latitude, decimal Longitude, decimal? Speed, decimal? Heading, DateTime Timestamp, string? DeviceId)?> GetDriverLocationAsync(Guid driverId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetDriversWithinRadiusAsync(decimal centerLat, decimal centerLng, double radiusKm, CancellationToken ct = default);
    Task RemoveDriverAsync(Guid driverId, CancellationToken ct = default);
}

public sealed class RedisLocationCache : IRedisLocationCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisLocationCache> _logger;
    
    private const string GeoSetKey = "fleet:locations";
    private const string MetaKeyPrefix = "driver:meta:";
    private static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(5);

    public RedisLocationCache(IConnectionMultiplexer redis, ILogger<RedisLocationCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SetDriverLocationAsync(
        Guid driverId, 
        decimal latitude, 
        decimal longitude, 
        decimal? speed, 
        decimal? heading, 
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var driverKey = driverId.ToString();

            // 1. Update geospatial set (for proximity queries)
            await db.GeoAddAsync(GeoSetKey, (double)longitude, (double)latitude, driverKey);

            // 2. Store metadata separately (speed, heading, timestamp)
            var metaKey = MetaKeyPrefix + driverKey;
            var metadata = new LocationMetadata
            {
                Speed = speed,
                Heading = heading,
                LastUpdate = DateTime.UtcNow,
                Latitude = latitude,
                Longitude = longitude,
                DeviceId = null // We might want to pass this in SetDriverLocationAsync too if needed
            };

            await db.StringSetAsync(
                metaKey, 
                JsonSerializer.Serialize(metadata), 
                MetaTtl);

            _logger.LogDebug("Updated location cache for driver {DriverId}", driverId);
        }
        catch (Exception ex)
        {
            // Non-critical: Log but don't throw
            // Location service should continue even if Redis fails
            _logger.LogError(ex, "Failed to update Redis cache for driver {DriverId}", driverId);
        }
    }

    public async Task<(decimal Latitude, decimal Longitude, decimal? Speed, decimal? Heading, DateTime Timestamp, string? DeviceId)?> GetDriverLocationAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var driverKey = driverId.ToString();
            
            // Try to get metadata first as it has everything
            var metaKey = MetaKeyPrefix + driverKey;
            var metaJson = await db.StringGetAsync(metaKey);

            if (!metaJson.IsNullOrEmpty)
            {
                var meta = JsonSerializer.Deserialize<LocationMetadata>(metaJson.ToString());
                if (meta != null)
                {
                    return (meta.Latitude, meta.Longitude, meta.Speed, meta.Heading, meta.LastUpdate, meta.DeviceId);
                }
            }

            // Fallback to basic GEO position if metadata missing
            var positions = await db.GeoPositionAsync(GeoSetKey, driverKey);

            if (positions.HasValue)
            {
                return ((decimal)positions.Value.Latitude, (decimal)positions.Value.Longitude, null, null, DateTime.UtcNow, null);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get location from Redis for driver {DriverId}", driverId);
            return null;
        }
    }

    private class LocationMetadata
    {
        public decimal? Speed { get; set; }
        public decimal? Heading { get; set; }
        public DateTime LastUpdate { get; set; }
        public decimal Latitude { get; set; }
        public decimal Longitude { get; set; }
        public string? DeviceId { get; set; }
    }

    public async Task<IReadOnlyList<Guid>> GetDriversWithinRadiusAsync(
        decimal centerLat, 
        decimal centerLng, 
        double radiusKm, 
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var results = await db.GeoRadiusAsync(
                GeoSetKey,
                (double)centerLng,
                (double)centerLat,
                radiusKm,
                GeoUnit.Kilometers);

            return results
                .Select(r => r.Member.ToString())
                .Where(s => !string.IsNullOrEmpty(s) && Guid.TryParse(s.AsSpan(), out _))
                .Select(s => Guid.Parse(s))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query drivers within radius");
            return Array.Empty<Guid>();
        }
    }

    public async Task RemoveDriverAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var driverKey = driverId.ToString();

            await Task.WhenAll(
                db.GeoRemoveAsync(GeoSetKey, driverKey),
                db.KeyDeleteAsync(MetaKeyPrefix + driverKey));

            _logger.LogDebug("Removed driver {DriverId} from location cache", driverId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove driver {DriverId} from cache", driverId);
        }
    }
}
