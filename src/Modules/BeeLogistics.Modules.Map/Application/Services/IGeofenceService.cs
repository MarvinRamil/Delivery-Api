using BeeLogistics.Modules.Map.Domain;

namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Service for geofence operations
/// Handles point-in-polygon and point-in-circle calculations
/// </summary>
public interface IGeofenceService
{
    /// <summary>
    /// Get all active geofences
    /// </summary>
    Task<List<Geofence>> GetActiveGeofencesAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if a point is inside a geofence
    /// </summary>
    Task<bool> IsPointInGeofenceAsync(decimal latitude, decimal longitude, Geofence geofence, CancellationToken ct = default);

    /// <summary>
    /// Get current geofence state for a driver
    /// </summary>
    Task<bool?> GetDriverGeofenceStateAsync(Guid driverId, Guid geofenceId, CancellationToken ct = default);

    /// <summary>
    /// Update driver geofence state
    /// </summary>
    Task UpdateDriverGeofenceStateAsync(Guid driverId, Guid geofenceId, bool isInside, CancellationToken ct = default);
}

