using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Map.Domain;

/// <summary>
/// Tracks the current geofence state for each driver
/// Used to detect entry/exit events
/// </summary>
public class DriverGeofenceState : Entity
{
    public Guid DriverId { get; private set; }
    public Guid GeofenceId { get; private set; }
    public bool IsInside { get; private set; }
    public DateTime LastStateChange { get; private set; }
    public DateTime LastChecked { get; private set; }

    private DriverGeofenceState() { } // EF Core

    public DriverGeofenceState(
        Guid driverId,
        Guid geofenceId,
        bool isInside)
    {
        DriverId = driverId;
        GeofenceId = geofenceId;
        IsInside = isInside;
        LastStateChange = DateTime.UtcNow;
        LastChecked = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void UpdateState(bool newIsInside)
    {
        if (IsInside != newIsInside)
        {
            IsInside = newIsInside;
            LastStateChange = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }
        LastChecked = DateTime.UtcNow;
    }
}

