namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Published when a booking is marked completed (all stops done).
/// Consumed by Fraud module for "driver completes 10 deliveries in 5 min" rule.
/// </summary>
public sealed record BookingCompletedEvent
{
    public required Guid BookingId { get; init; }
    public required Guid DriverId { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateTime CompletedAt { get; init; }
    /// <summary>
    /// The final fare of the booking. Used as fallback for earnings calculation
    /// when the payment record is not yet available (e.g. cashless payment webhook pending).
    /// </summary>
    public decimal? FinalFare { get; init; }
}

/// <summary>
/// Published when a new booking/order is created.
/// Consumed by Fraud module for "account creates 20 orders in 2 min" rule.
/// </summary>
public sealed record OrderCreatedEvent
{
    public required Guid BookingId { get; init; }
    public required Guid CustomerId { get; init; }
    public required DateTime CreatedAt { get; init; }
}

/// <summary>
/// Published when a user registers (after successful registration).
/// Consumed by Fraud module for "same device registers 5 users" rule.
/// </summary>
public sealed record UserRegisteredEvent
{
    public required Guid UserId { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceFingerprint { get; init; }
    public required DateTime RegisteredAt { get; init; }
}
