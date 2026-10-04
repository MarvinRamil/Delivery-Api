using System;
using BeeLogistics.Shared.Abstractions;
using NetTopologySuite.Geometries;

namespace BeeLogistics.Modules.Map.Domain;

public class DriverLocation : Entity
{
    public Guid DriverId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public decimal? Speed { get; private set; } // km/h
    public decimal? Heading { get; private set; } // degrees (0-360)
    public DateTime Timestamp { get; private set; }
    public string? DeviceId { get; set; }
    
    // PostGIS geometry column (managed by EF Core, populated from Latitude/Longitude)
    // This enables efficient spatial queries using PostGIS functions
    public Point? Location { get; private set; }

    private DriverLocation() { }

    public DriverLocation(
        Guid driverId,
        decimal latitude,
        decimal longitude,
        decimal? speed = null,
        decimal? heading = null,
        string? deviceId = null)
    {
        DriverId = driverId;
        Latitude = latitude;
        Longitude = longitude;
        Speed = speed;
        Heading = heading;
        DeviceId = deviceId;
        Timestamp = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void UpdateLocation(decimal latitude, decimal longitude, decimal? speed = null, decimal? heading = null)
    {
        Latitude = latitude;
        Longitude = longitude;
        Speed = speed;
        Heading = heading;
        Timestamp = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        
        // Update PostGIS geometry from lat/lng (EF Core will handle this via computed column or trigger)
        // For now, we'll let the migration handle the initial population
    }
}
