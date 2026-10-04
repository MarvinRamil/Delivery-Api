using BeeLogistics.Modules.Map.Domain;

namespace BeeLogistics.Modules.Map.Application.Events;

/// <summary>
/// Event published to RabbitMQ when driver location is updated
/// This is the contract between MQTT subscriber and MassTransit consumers
/// </summary>
public sealed record DriverLocationUpdatedEvent
{
    public required Guid DriverId { get; init; }
    public required decimal Latitude { get; init; }
    public required decimal Longitude { get; init; }
    public decimal? Speed { get; init; }
    public decimal? Heading { get; init; }
    public decimal? Accuracy { get; init; }
    /// <summary>When the fix was taken on the device (UTC), not when it was received.</summary>
    public DateTime Timestamp { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Which transport carried this point. Persisted to history so we can tell whether the HTTP fallback is firing.</summary>
    public LocationSource Source { get; init; } = LocationSource.Mqtt;

    /// <summary>
    /// Flag to indicate if this is a historical/buffered update
    /// Used when mobile app reconnects after disconnection.
    ///
    /// Historical points are written to history only. They deliberately do not touch live state -
    /// Redis, the H3 geo index used for offer matching, the DriverLocation snapshot, the
    /// ApplicationUser coordinates, or the SignalR broadcast - because replaying a backlog through
    /// those would rewind the driver's current position, match them against offers from where they
    /// were half an hour ago, and jitter the tracking customer's map through the whole old trail.
    /// </summary>
    public bool IsHistorical { get; init; } = false;
}
