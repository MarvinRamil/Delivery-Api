using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <summary>
/// Geofence service implementation
/// Handles geofence calculations and state management
/// </summary>
public sealed class GeofenceService : IGeofenceService
{
    private readonly IGeofenceRepository _geofenceRepository;
    private readonly IDriverGeofenceStateRepository _stateRepository;
    private readonly IDistanceCalculationService _distanceService;
    private readonly ILogger<GeofenceService> _logger;

    public GeofenceService(
        IGeofenceRepository geofenceRepository,
        IDriverGeofenceStateRepository stateRepository,
        IDistanceCalculationService distanceService,
        ILogger<GeofenceService> logger)
    {
        _geofenceRepository = geofenceRepository;
        _stateRepository = stateRepository;
        _distanceService = distanceService;
        _logger = logger;
    }

    public async Task<List<Geofence>> GetActiveGeofencesAsync(CancellationToken ct = default)
    {
        return await _geofenceRepository.GetActiveGeofencesAsync(ct);
    }

    public async Task<bool> IsPointInGeofenceAsync(decimal latitude, decimal longitude, Geofence geofence, CancellationToken ct = default)
    {
        return geofence.Type switch
        {
            GeofenceType.Circle => await IsPointInCircleAsync(latitude, longitude, geofence, ct),
            GeofenceType.Polygon => IsPointInPolygon(latitude, longitude, geofence),
            _ => false
        };
    }

    public async Task<bool?> GetDriverGeofenceStateAsync(Guid driverId, Guid geofenceId, CancellationToken ct = default)
    {
        var state = await _stateRepository.GetByDriverAndGeofenceAsync(driverId, geofenceId, ct);
        return state?.IsInside;
    }

    public async Task UpdateDriverGeofenceStateAsync(Guid driverId, Guid geofenceId, bool isInside, CancellationToken ct = default)
    {
        try
        {
            var state = await _stateRepository.GetByDriverAndGeofenceAsync(driverId, geofenceId, ct);
            
            if (state == null)
            {
                state = new DriverGeofenceState(driverId, geofenceId, isInside);
                _stateRepository.Add(state);
            }
            else
            {
                state.UpdateState(isInside);
            }

            await _stateRepository.SaveChangesAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table does not exist
        {
            // Table doesn't exist yet - log and continue
            _logger.LogWarning("Geofence state table does not exist yet. Skipping state update for driver {DriverId}, geofence {GeofenceId}", 
                driverId, geofenceId);
        }
        catch (Exception ex)
        {
            // Log other errors but don't throw - geofence checking shouldn't break location updates
            _logger.LogError(ex, "Error updating geofence state for driver {DriverId}, geofence {GeofenceId}", 
                driverId, geofenceId);
        }
    }

    private async Task<bool> IsPointInCircleAsync(decimal latitude, decimal longitude, Geofence geofence, CancellationToken ct)
    {
        if (!geofence.CenterLatitude.HasValue || 
            !geofence.CenterLongitude.HasValue || 
            !geofence.RadiusMeters.HasValue)
        {
            return false;
        }

        // Use PostGIS distance service for accurate calculation
        var distanceKm = await _distanceService.CalculateDistanceAsync(
            geofence.CenterLatitude.Value,
            geofence.CenterLongitude.Value,
            latitude,
            longitude,
            ct);

        // Convert radius from meters to kilometers for comparison
        var radiusKm = geofence.RadiusMeters.Value / 1000m;

        return distanceKm <= radiusKm;
    }

    private bool IsPointInPolygon(decimal latitude, decimal longitude, Geofence geofence)
    {
        if (string.IsNullOrEmpty(geofence.PolygonCoordinates))
        {
            return false;
        }

        try
        {
            // Parse polygon coordinates from JSON
            // Expected format: [[lat, lng], [lat, lng], ...]
            var coordinates = JsonSerializer.Deserialize<List<List<decimal>>>(geofence.PolygonCoordinates);
            
            if (coordinates == null || coordinates.Count < 3)
            {
                _logger.LogWarning("Invalid polygon coordinates for geofence {GeofenceId}", geofence.Id);
                return false;
            }

            // Ray casting algorithm for point-in-polygon
            var pointLat = (double)latitude;
            var pointLng = (double)longitude;
            var inside = false;

            for (int i = 0, j = coordinates.Count - 1; i < coordinates.Count; j = i++)
            {
                var xi = (double)coordinates[i][0];
                var yi = (double)coordinates[i][1];
                var xj = (double)coordinates[j][0];
                var yj = (double)coordinates[j][1];

                var intersect = ((yi > pointLat) != (yj > pointLat)) &&
                               (pointLng < (xj - xi) * (pointLat - yi) / (yj - yi) + xi);

                if (intersect)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking point in polygon for geofence {GeofenceId}", geofence.Id);
            return false;
        }
    }

}

