using BeeLogistics.Modules.Fraud.Domain;

namespace BeeLogistics.Modules.Fraud.Application.DTOs;

public record FraudEventDto(
    Guid Id,
    FraudEventType EventType,
    Guid ActorId,
    DateTime OccurredAt,
    Guid? BookingId,
    Guid? DriverId,
    Guid? CustomerId,
    Guid? UserId,
    string? DeviceId,
    decimal? Latitude,
    decimal? Longitude,
    decimal? PreviousLatitude,
    decimal? PreviousLongitude,
    decimal? DistanceKm,
    string? MetadataJson,
    DateTime CreatedAt
);
