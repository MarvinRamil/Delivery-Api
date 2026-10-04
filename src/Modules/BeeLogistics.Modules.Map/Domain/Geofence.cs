using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Map.Domain;

/// <summary>
/// Geofence entity representing a geographic zone
/// Supports polygon and circle geofences
/// </summary>
public class Geofence : Entity
{
    public string Name { get; private set; } = null!;
    public GeofenceType Type { get; private set; }
    public bool IsActive { get; private set; }
    
    // For polygon geofences: JSON array of coordinates [[lat, lng], ...]
    public string? PolygonCoordinates { get; private set; }
    
    // For circle geofences
    public decimal? CenterLatitude { get; private set; }
    public decimal? CenterLongitude { get; private set; }
    public decimal? RadiusMeters { get; private set; }
    
    // Metadata (JSON) for additional properties
    public string? Metadata { get; private set; }
    
    // Optional: Service area, restricted zone, pickup zone, etc.
    public string? Category { get; private set; }

    private Geofence() { } // EF Core

    public Geofence(
        string name,
        GeofenceType type,
        bool isActive = true,
        string? category = null)
    {
        Name = name;
        Type = type;
        IsActive = isActive;
        Category = category;
        CreatedAt = DateTime.UtcNow;
    }

    public void SetPolygonCoordinates(string polygonCoordinatesJson)
    {
        if (Type != GeofenceType.Polygon)
        {
            throw new InvalidOperationException("Cannot set polygon coordinates for non-polygon geofence");
        }
        PolygonCoordinates = polygonCoordinatesJson;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetCircleCoordinates(decimal centerLatitude, decimal centerLongitude, decimal radiusMeters)
    {
        if (Type != GeofenceType.Circle)
        {
            throw new InvalidOperationException("Cannot set circle coordinates for non-circle geofence");
        }
        CenterLatitude = centerLatitude;
        CenterLongitude = centerLongitude;
        RadiusMeters = radiusMeters;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetMetadata(string metadataJson)
    {
        Metadata = metadataJson;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateName(string name)
    {
        Name = name;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Geofence types
/// </summary>
public enum GeofenceType
{
    Polygon = 1,  // Polygon defined by coordinates
    Circle = 2    // Circle defined by center + radius
}

