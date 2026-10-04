namespace BeeLogistics.Modules.Map.Application.Events;

/// <summary>
/// Event published when a driver enters or exits a geofence
/// </summary>
public sealed record DriverGeofenceStateChangedEvent
{
    public required Guid DriverId { get; init; }
    public required Guid GeofenceId { get; init; }
    public required string GeofenceName { get; init; }
    public required GeofenceEventType EventType { get; init; } // Entered or Exited
    public required decimal Latitude { get; init; }
    public required decimal Longitude { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Category { get; init; }
}

/// <summary>
/// Type of geofence event
/// </summary>
public enum GeofenceEventType
{
    Entered = 1,
    Exited = 2
}

