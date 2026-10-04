using BeeLogistics.Shared.Abstractions;
using NetTopologySuite.Geometries;

namespace BeeLogistics.Modules.Map.Domain;

/// <summary>
/// Historical record of driver location (immutable)
/// Optimized for time-series queries and analytics
/// </summary>
public sealed class DriverLocationHistory : Entity
{
    public Guid DriverId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public decimal? Speed { get; private set; }
    public decimal? Heading { get; private set; }
    /// <summary>When the fix was taken on the device, not when the server stored it.</summary>
    public DateTime Timestamp { get; private set; }

    public string? DeviceId { get; private set; }

    /// <summary>Reported GPS accuracy in metres, when the device supplied one.</summary>
    public decimal? Accuracy { get; private set; }

    /// <summary>
    /// Which transport carried this point. Makes "is the HTTP fallback firing?" a query rather than
    /// a guess - the question that could not be answered while the fallback was silently dropping
    /// everything (GitLab #37).
    /// </summary>
    public LocationSource Source { get; private set; }

    /// <summary>True when this point was backfilled from the app's offline buffer rather than received live.</summary>
    public bool IsHistorical { get; private set; }

    // PostGIS geometry column (managed by EF Core, populated from Latitude/Longitude)
    public Point? Location { get; private set; }

    private DriverLocationHistory() { } // EF Core

    public DriverLocationHistory(
        Guid driverId,
        decimal latitude,
        decimal longitude,
        decimal? speed,
        decimal? heading,
        string? deviceId,
        DateTime timestamp,
        decimal? accuracy = null,
        LocationSource source = LocationSource.Mqtt,
        bool isHistorical = false)
    {
        DriverId = driverId;
        Latitude = latitude;
        Longitude = longitude;
        Speed = speed;
        Heading = heading;
        DeviceId = deviceId;
        Timestamp = timestamp;
        Accuracy = accuracy;
        Source = source;
        IsHistorical = isHistorical;
        CreatedAt = DateTime.UtcNow;
    }
}
