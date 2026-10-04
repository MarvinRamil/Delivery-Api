namespace BeeLogistics.Modules.Map.Application.DTOs;

public record LocationDto(
    Guid DriverId,
    decimal Latitude,
    decimal Longitude,
    decimal? Speed,
    decimal? Heading,
    DateTime Timestamp,
    string? DeviceId
);

/// <param name="RecordedAt">
/// When the fix was actually taken on the device, UTC. Optional: when absent the server falls back
/// to <c>DateTime.UtcNow</c>, which is what it always did and is correct for a live update.
///
/// It matters for /api/locations/batch. Those points were buffered while the app was backgrounded
/// and MQTT was unavailable, so they can be many minutes old by the time they are flushed. Without
/// this the server stamped every point in the batch with the flush instant, collapsing an entire
/// trail into a single moment and making the history useless for route replay or distance travelled.
/// </param>
/// <param name="Accuracy">Reported GPS accuracy in metres, if the device supplied one.</param>
public record UpdateLocationDto(
    decimal Latitude,
    decimal Longitude,
    decimal? Speed = null,
    decimal? Heading = null,
    string? DeviceId = null,
    DateTime? RecordedAt = null,
    decimal? Accuracy = null
);

public record LocationHistoryDto(
    Guid Id,
    Guid DriverId,
    decimal Latitude,
    decimal Longitude,
    decimal? Speed,
    decimal? Heading,
    DateTime Timestamp,
    string? DeviceId
);
