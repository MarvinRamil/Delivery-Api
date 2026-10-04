namespace BeeLogistics.Modules.Map.Application.DTOs;

/// <summary>
/// Lightweight DTO for location updates from MQTT
/// Optimized for minimal allocation and fast deserialization
/// </summary>
public sealed record DriverLocationUpdateDto
{
    public required Guid DriverId { get; init; }
    public required decimal Latitude { get; init; }
    public required decimal Longitude { get; init; }
    public decimal? Speed { get; init; }
    public decimal? Heading { get; init; }
    public decimal? Accuracy { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? DeviceId { get; init; }
}
