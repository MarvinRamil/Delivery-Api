using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Fraud.Domain;

/// <summary>
/// Raw event stored for fraud rules and future ML training.
/// </summary>
public class FraudEvent : Entity
{
    public FraudEventType EventType { get; private set; }
    public Guid ActorId { get; private set; }
    public DateTime OccurredAt { get; private set; }

    public Guid? BookingId { get; private set; }
    public Guid? DriverId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? UserId { get; private set; }
    public string? DeviceId { get; private set; }

    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public decimal? PreviousLatitude { get; private set; }
    public decimal? PreviousLongitude { get; private set; }
    public decimal? DistanceKm { get; private set; }

    public string? MetadataJson { get; private set; }

    private FraudEvent() { }

    public static FraudEvent DeliveryCompleted(Guid bookingId, Guid driverId, Guid customerId, DateTime occurredAt)
    {
        return new FraudEvent
        {
            Id = Guid.NewGuid(),
            EventType = FraudEventType.DeliveryCompleted,
            ActorId = driverId,
            OccurredAt = occurredAt,
            BookingId = bookingId,
            DriverId = driverId,
            CustomerId = customerId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static FraudEvent LocationUpdated(Guid driverId, DateTime occurredAt, decimal lat, decimal lng,
        decimal? prevLat, decimal? prevLng, decimal? distanceKm, string? deviceId)
    {
        return new FraudEvent
        {
            Id = Guid.NewGuid(),
            EventType = FraudEventType.LocationUpdated,
            ActorId = driverId,
            OccurredAt = occurredAt,
            DriverId = driverId,
            Latitude = lat,
            Longitude = lng,
            PreviousLatitude = prevLat,
            PreviousLongitude = prevLng,
            DistanceKm = distanceKm,
            DeviceId = deviceId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static FraudEvent OrderCreated(Guid bookingId, Guid customerId, DateTime occurredAt)
    {
        return new FraudEvent
        {
            Id = Guid.NewGuid(),
            EventType = FraudEventType.OrderCreated,
            ActorId = customerId,
            OccurredAt = occurredAt,
            BookingId = bookingId,
            CustomerId = customerId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static FraudEvent UserRegistered(Guid userId, DateTime occurredAt, string? deviceId)
    {
        return new FraudEvent
        {
            Id = Guid.NewGuid(),
            EventType = FraudEventType.UserRegistered,
            ActorId = userId,
            OccurredAt = occurredAt,
            UserId = userId,
            DeviceId = deviceId,
            CreatedAt = DateTime.UtcNow
        };
    }
}
