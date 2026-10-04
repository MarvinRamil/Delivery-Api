using BeeLogistics.Modules.Map.Application.Interfaces;
using H3;
using H3.Algorithms;
using H3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using StackExchange.Redis;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Redis-backed implementation of <see cref="IDriverGeoIndex"/> using Uber H3 cells.
///
/// Layout:
///   h3:cell:{cellHex}   - sorted set of driver ids in that cell, score = unix seconds of last update
///   h3:driver:{driverId} - the driver's current cell (so a move removes the old entry)
///
/// Resolution 8 cells are ~0.74 km^2 (~460 m edge); ring k around a cell covers
/// roughly (1 + 3k(k+1)) cells, so k=3 at res 8 is about a 3 km search radius.
/// Stale entries are filtered by score on read and trimmed opportunistically,
/// so a driver that stops reporting disappears from results after the staleness
/// window without any explicit cleanup job.
/// </summary>
public sealed class H3DriverGeoIndex : IDriverGeoIndex
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IRedisLocationCache _locationCache;
    private readonly ILogger<H3DriverGeoIndex> _logger;

    private readonly int _resolution;
    private readonly int _maxRings;
    private readonly int _maxRingsHardCap;
    private readonly TimeSpan _staleness;

    private const string CellKeyPrefix = "h3:cell:";
    private const string DriverCellKeyPrefix = "h3:driver:";

    public H3DriverGeoIndex(
        IConnectionMultiplexer redis,
        IRedisLocationCache locationCache,
        IConfiguration configuration,
        ILogger<H3DriverGeoIndex> logger)
    {
        _redis = redis;
        _locationCache = locationCache;
        _logger = logger;

        _resolution = configuration.GetValue("Map:H3:Resolution", 8);
        _maxRings = configuration.GetValue("Map:H3:MaxRings", 3);
        // Safety ceiling applied regardless of maxRingsOverride, so a misconfigured or
        // runaway per-call radius can't fan out to an unbounded number of Redis reads.
        _maxRingsHardCap = configuration.GetValue("Map:H3:MaxRingsHardCap", 12);
        _staleness = TimeSpan.FromMinutes(configuration.GetValue("Map:H3:StalenessMinutes", 5));
    }

    public async Task IndexDriverAsync(Guid driverId, decimal latitude, decimal longitude, CancellationToken ct = default)
    {
        try
        {
            var cell = ToCell(latitude, longitude);
            if (cell == H3Index.Invalid)
            {
                _logger.LogWarning("H3 index rejected coordinates ({Lat}, {Lng}) for driver {DriverId}", latitude, longitude, driverId);
                return;
            }

            var db = _redis.GetDatabase();
            var driverKey = driverId.ToString();
            var cellHex = cell.ToString();
            var driverCellKey = DriverCellKeyPrefix + driverKey;

            // Track the driver's current cell so a move can clean up the previous one.
            var previousCell = await db.StringSetAndGetAsync(driverCellKey, cellHex, _staleness * 2);
            if (!previousCell.IsNullOrEmpty && previousCell.ToString() != cellHex)
            {
                await db.SortedSetRemoveAsync(CellKeyPrefix + previousCell, driverKey);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cellKey = CellKeyPrefix + cellHex;
            await db.SortedSetAddAsync(cellKey, driverKey, now);
            // Expire the whole cell set well past the staleness window so empty
            // cells don't accumulate; refreshed on every write to the cell.
            await db.KeyExpireAsync(cellKey, _staleness * 4);
        }
        catch (Exception ex)
        {
            // Non-critical: the matching path falls back to a DB scan when the
            // index is unavailable, so never fail the location pipeline here.
            _logger.LogError(ex, "Failed to H3-index location for driver {DriverId}", driverId);
        }
    }

    public async Task<IReadOnlyList<DriverGeoCandidate>> FindNearestDriversAsync(
        decimal latitude,
        decimal longitude,
        int maxResults,
        int? maxRingsOverride = null,
        CancellationToken ct = default)
    {
        try
        {
            var origin = ToCell(latitude, longitude);
            if (origin == H3Index.Invalid || maxResults <= 0)
                return Array.Empty<DriverGeoCandidate>();

            var db = _redis.GetDatabase();
            var cutoff = DateTimeOffset.UtcNow.Subtract(_staleness).ToUnixTimeSeconds();
            var rings = Math.Min(maxRingsOverride ?? _maxRings, _maxRingsHardCap);

            // Cells within `rings` of the origin, grouped by ring distance so we
            // can stop expanding once a completed ring has produced enough candidates.
            var ringGroups = origin
                .GridDiskDistances(rings)
                .GroupBy(rc => rc.Distance)
                .OrderBy(g => g.Key);

            var driverIds = new HashSet<Guid>();
            foreach (var ring in ringGroups)
            {
                var reads = ring
                    .Select(rc => db.SortedSetRangeByScoreAsync(CellKeyPrefix + rc.Index, cutoff, double.PositiveInfinity))
                    .ToArray();
                var results = await Task.WhenAll(reads);

                foreach (var member in results.SelectMany(r => r))
                {
                    if (Guid.TryParse(member.ToString(), out var id))
                        driverIds.Add(id);
                }

                if (driverIds.Count >= maxResults)
                    break;
            }

            if (driverIds.Count == 0)
                return Array.Empty<DriverGeoCandidate>();

            // Rank candidates by exact great-circle distance using their last
            // known position from the location cache.
            var candidates = new List<DriverGeoCandidate>(driverIds.Count);
            foreach (var driverId in driverIds)
            {
                var location = await _locationCache.GetDriverLocationAsync(driverId, ct);
                if (location == null)
                    continue;

                var distanceKm = HaversineKm(
                    (double)latitude, (double)longitude,
                    (double)location.Value.Latitude, (double)location.Value.Longitude);

                candidates.Add(new DriverGeoCandidate(driverId, location.Value.Latitude, location.Value.Longitude, distanceKm));
            }

            return candidates
                .OrderBy(c => c.DistanceKm)
                .Take(maxResults)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "H3 nearest-driver query failed for ({Lat}, {Lng})", latitude, longitude);
            return Array.Empty<DriverGeoCandidate>();
        }
    }

    public async Task RemoveDriverAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var driverKey = driverId.ToString();
            var driverCellKey = DriverCellKeyPrefix + driverKey;

            var cell = await db.StringGetAsync(driverCellKey);
            if (!cell.IsNullOrEmpty)
                await db.SortedSetRemoveAsync(CellKeyPrefix + cell, driverKey);

            await db.KeyDeleteAsync(driverCellKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove driver {DriverId} from H3 index", driverId);
        }
    }

    private H3Index ToCell(decimal latitude, decimal longitude) =>
        // NTS Coordinate is (x = longitude, y = latitude)
        H3Index.FromLatLng(LatLng.FromCoordinate(new Coordinate((double)longitude, (double)latitude)), _resolution);

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
