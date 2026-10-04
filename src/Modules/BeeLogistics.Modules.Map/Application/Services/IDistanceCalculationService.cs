namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Service for calculating distances between geographic coordinates.
/// Uses PostGIS for accurate and performant distance calculations.
/// </summary>
public interface IDistanceCalculationService
{
    /// <summary>
    /// Calculates the distance between two geographic points.
    /// </summary>
    /// <param name="lat1">Latitude of first point</param>
    /// <param name="lon1">Longitude of first point</param>
    /// <param name="lat2">Latitude of second point</param>
    /// <param name="lon2">Longitude of second point</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Distance in kilometers</returns>
    Task<decimal> CalculateDistanceAsync(
        decimal lat1, 
        decimal lon1, 
        decimal lat2, 
        decimal lon2, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Calculates distances from multiple driver locations to a center point.
    /// Optimized for batch calculations using PostGIS.
    /// </summary>
    /// <param name="centerLat">Latitude of center point</param>
    /// <param name="centerLon">Longitude of center point</param>
    /// <param name="driverLocations">Collection of driver locations (DriverId, Lat, Lon)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of (DriverId, Distance in kilometers) ordered by distance</returns>
    Task<IReadOnlyList<(Guid DriverId, decimal Distance)>> CalculateDistancesToPointAsync(
        decimal centerLat, 
        decimal centerLon,
        IEnumerable<(Guid DriverId, decimal Lat, decimal Lon)> driverLocations,
        CancellationToken ct = default);
}
