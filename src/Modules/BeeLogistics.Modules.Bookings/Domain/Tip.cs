using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class Tip : Entity
{
    public Guid BookingId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid DriverId { get; private set; }
    public decimal Amount { get; private set; }
    public DateTime TippedAt { get; private set; }
    public string? Message { get; private set; }

    // Navigation
    public Booking Booking { get; private set; } = null!;

    private Tip() { }

    public Tip(
        Guid bookingId,
        Guid customerId,
        Guid driverId,
        decimal amount,
        string? message = null)
    {
        if (amount <= 0)
            throw new ArgumentException("Tip amount must be positive", nameof(amount));

        BookingId = bookingId;
        CustomerId = customerId;
        DriverId = driverId;
        Amount = amount;
        Message = message;
        TippedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}
