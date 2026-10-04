using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeeLogistics.Modules.Map.Infrastructure.Repositories;

public class LocationRepository : Repository<DriverLocation, MapDbContext>, ILocationRepository
{
    public LocationRepository(MapDbContext context) : base(context) { }

    public async Task<DriverLocation?> GetCurrentLocationByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        try
        {
            return await DbSet
                .Where(l => l.DriverId == driverId)
                .OrderByDescending(l => l.Timestamp)
                .FirstOrDefaultAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42703") // Column does not exist (DeletedAt)
        {
            // DeletedAt column doesn't exist yet - migration not run
            // Return null so consumer will create a new record
            // This is a temporary workaround until migration is run
            return null;
        }
    }

    /// <summary>Upper bound on a single history response, whatever range is asked for.</summary>
    public const int MaxHistoryPoints = 500;

    public async Task<IReadOnlyList<DriverLocationHistory>> GetLocationHistoryByDriverIdAsync(
        Guid driverId,
        DateTime? from = null,
        DateTime? to = null,
        int limit = MaxHistoryPoints,
        CancellationToken ct = default)
    {
        // LocationHistory, not DbSet. DbSet on this repository is DriverLocations - the snapshot,
        // one row per driver updated in place - so querying it returned the driver's current
        // position rather than a trail, regardless of the range (GitLab #39).
        var query = Context.LocationHistory
            .Where(l => l.DriverId == driverId);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .Take(Math.Clamp(limit, 1, MaxHistoryPoints))
            .ToListAsync(ct);
    }

    public async Task<int> DeleteLocationHistoryOlderThanAsync(DateTime olderThanUtc, int maxRows, CancellationToken ct = default)
    {
        if (maxRows < 1)
            return 0;

        // Bounded set-based delete. ExecuteDeleteAsync issues a single DELETE without loading
        // entities, and the subquery caps how many rows one statement touches so a large backlog
        // cannot hold locks on the highest-volume table in the schema for an unbounded stretch.
        return await Context.LocationHistory
            .Where(l => Context.LocationHistory
                .Where(x => x.Timestamp < olderThanUtc)
                .OrderBy(x => x.Timestamp)
                .Take(maxRows)
                .Select(x => x.Id)
                .Contains(l.Id))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<DriverLocation>> GetLocationsByDriverIdsAsync(
        IEnumerable<Guid> driverIds,
        CancellationToken ct = default)
    {
        var driverIdsList = driverIds.ToList();
        
        // Get most recent location for each driver
        var locations = await DbSet
            .Where(l => driverIdsList.Contains(l.DriverId))
            .GroupBy(l => l.DriverId)
            .Select(g => g.OrderByDescending(l => l.Timestamp).First())
            .ToListAsync(ct);

        return locations;
    }

    public async Task<IReadOnlyList<DriverLocation>> GetLocationsWithinRadiusAsync(
        decimal centerLatitude,
        decimal centerLongitude,
        decimal radiusKm,
        CancellationToken ct = default)
    {
        // Use PostGIS ST_DWithin for efficient spatial queries
        // This is much faster than Haversine formula as it uses spatial indexes (GIST)
        // ST_DWithin uses meters, so we convert radiusKm to meters
        var radiusMeters = (double)radiusKm * 1000.0;
        var centerLon = (double)centerLongitude;
        var centerLat = (double)centerLatitude;
        
        // Optimized SQL using DISTINCT ON to get most recent location per driver in database
        // This eliminates the need for in-memory grouping, improving performance
        // DISTINCT ON is a PostgreSQL-specific feature that's perfect for this use case
        var sql = $@"
            SELECT DISTINCT ON (""DriverId"") *
            FROM map.""DriverLocations""
            WHERE ""Location"" IS NOT NULL
              AND ST_DWithin(""Location""::geography, ST_SetSRID(ST_MakePoint({{0}}, {{1}}), 4326)::geography, {{2}})
              AND ""IsDeleted"" = false
            ORDER BY ""DriverId"", ""Timestamp"" DESC
        ";
        
        // Execute raw SQL with parameters (EF Core will properly escape values)
        // DISTINCT ON ensures we get only the most recent location for each driver
        var locations = await DbSet
            .FromSqlRaw(sql, centerLon, centerLat, radiusMeters)
            .ToListAsync(ct);

        return locations;
    }
}
