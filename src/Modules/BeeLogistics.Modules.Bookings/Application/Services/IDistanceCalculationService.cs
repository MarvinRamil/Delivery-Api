using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Service for calculating distances between delivery stops for pricing purposes.
/// Follows Interface Segregation Principle (ISP) - single responsibility.
/// Renamed to avoid conflict with Map module's IDistanceCalculationService.
/// </summary>
public interface IRouteDistanceCalculationService
{
    /// <summary>
    /// Calculates the total distance in kilometers for a list of stops.
    /// </summary>
    Task<decimal> CalculateRouteDistanceAsync(List<DeliveryStop> stops, CancellationToken ct = default);
}
