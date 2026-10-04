using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Default implementation using Haversine formula for route distance calculation.
/// Can be replaced with Google Maps API or other services.
/// Follows Single Responsibility Principle (SRP) - only handles route distance calculation.
/// </summary>
public class RouteDistanceCalculationService : IRouteDistanceCalculationService
{
    private const decimal EarthRadiusKm = 6371;

    public Task<decimal> CalculateRouteDistanceAsync(List<DeliveryStop> stops, CancellationToken ct = default)
    {
        if (stops == null || stops.Count < 2)
            return Task.FromResult(0m);

        decimal totalDistance = 0;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var from = stops[i];
            var to = stops[i + 1];

            if (from.Latitude.HasValue && from.Longitude.HasValue &&
                to.Latitude.HasValue && to.Longitude.HasValue)
            {
                var distance = CalculateHaversineDistance(
                    from.Latitude.Value, from.Longitude.Value,
                    to.Latitude.Value, to.Longitude.Value);
                totalDistance += distance;
            }
            else
            {
                // Fallback: estimate 5km per stop if coordinates not available
                totalDistance += 5;
            }
        }

        return Task.FromResult(Math.Round(totalDistance, 2));
    }

    private decimal CalculateHaversineDistance(decimal lat1, decimal lon1, decimal lat2, decimal lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin((double)(dLat / 2)) * Math.Sin((double)(dLat / 2)) +
                Math.Cos((double)ToRadians(lat1)) * Math.Cos((double)ToRadians(lat2)) *
                Math.Sin((double)(dLon / 2)) * Math.Sin((double)(dLon / 2));

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (decimal)((double)EarthRadiusKm * c);
    }

    private decimal ToRadians(decimal degrees)
    {
        return degrees * (decimal)(Math.PI / 180.0);
    }
}
