using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// PostGIS-based distance calculation service.
/// Uses database-level PostGIS functions for accurate and performant distance calculations.
/// </summary>
public class PostGISDistanceService : IDistanceCalculationService
{
    private readonly MapDbContext _context;
    private readonly ILogger<PostGISDistanceService> _logger;

    public PostGISDistanceService(MapDbContext context, ILogger<PostGISDistanceService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<decimal> CalculateDistanceAsync(
        decimal lat1, 
        decimal lon1, 
        decimal lat2, 
        decimal lon2, 
        CancellationToken ct = default)
    {
        try
        {
            // Use PostGIS ST_Distance with geography type for accurate Earth-surface distance
            // Returns distance in meters, convert to kilometers
            // Using FormattableString for proper parameterization
            // EF Core SqlQuery<double> wraps this in SELECT s."Value" FROM (...) AS s, so the column must be named "Value"
            FormattableString sql = $@"
                SELECT ST_Distance(
                    ST_SetSRID(ST_MakePoint({lon1}, {lat1}), 4326)::geography,
                    ST_SetSRID(ST_MakePoint({lon2}, {lat2}), 4326)::geography
                ) / 1000.0 AS ""Value""
            ";

            var distanceKm = await _context.Database
                .SqlQuery<double>(sql)
                .FirstOrDefaultAsync(ct);

            return (decimal)distanceKm;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error calculating distance between ({Lat1}, {Lon1}) and ({Lat2}, {Lon2})", 
                lat1, lon1, lat2, lon2);
            
            // Fallback to Haversine if PostGIS fails (shouldn't happen, but safety net)
            return CalculateHaversineDistance(lat1, lon1, lat2, lon2);
        }
    }

    public async Task<IReadOnlyList<(Guid DriverId, decimal Distance)>> CalculateDistancesToPointAsync(
        decimal centerLat, 
        decimal centerLon,
        IEnumerable<(Guid DriverId, decimal Lat, decimal Lon)> driverLocations,
        CancellationToken ct = default)
    {
        var locations = driverLocations.ToList();
        if (!locations.Any())
            return Array.Empty<(Guid, decimal)>();

        try
        {
            // Use PostGIS to calculate distances in batch
            // This is more efficient than individual calculations
            var results = new List<(Guid DriverId, decimal Distance)>();

            foreach (var (driverId, lat, lon) in locations)
            {
                var distance = await CalculateDistanceAsync(centerLat, centerLon, lat, lon, ct);
                results.Add((driverId, distance));
            }

            // Return sorted by distance (closest first)
            return results.OrderBy(r => r.Distance).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Error calculating distances from center ({CenterLat}, {CenterLon}) to {Count} driver locations", 
                centerLat, centerLon, locations.Count);
            
            // Fallback to Haversine for all locations
            return locations
                .Select(loc => (loc.DriverId, Distance: CalculateHaversineDistance(centerLat, centerLon, loc.Lat, loc.Lon)))
                .OrderBy(r => r.Distance)
                .ToList();
        }
    }

    /// <summary>
    /// Fallback Haversine distance calculation.
    /// Used when PostGIS is unavailable or fails.
    /// </summary>
    private static decimal CalculateHaversineDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        const double R = 6371; // Earth's radius in kilometers
        var dLat = (double)(lat2 - lat1) * Math.PI / 180;
        var dLon = (double)(lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos((double)lat1 * Math.PI / 180) * Math.Cos((double)lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (decimal)(R * c);
    }
}
