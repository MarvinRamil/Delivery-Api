using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class DeliveryStop : Entity
{
    public Guid BookingId { get; internal set; } // Changed to internal set for EF Core and domain operations
    public int Sequence { get; private set; } // 0 = pickup, 1+ = dropoffs
    public string Address { get; private set; } = null!;
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public string? ContactName { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? Notes { get; private set; }
    public StopType Type { get; private set; }
    public StopStatus Status { get; private set; }
    public DateTime? ArrivedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    // Navigation property
    public Booking Booking { get; private set; } = null!;

    private DeliveryStop() { }

    public DeliveryStop(
        Guid bookingId,
        int sequence,
        string address,
        StopType type,
        decimal? latitude = null,
        decimal? longitude = null,
        string? contactName = null,
        string? contactPhone = null,
        string? notes = null)
    {
        // Note: bookingId can be Guid.Empty temporarily when creating stops before booking is saved.
        // EF Core will set the correct BookingId when the booking is saved due to the navigation property relationship.
        BookingId = bookingId;
        Sequence = sequence;
        Address = address;
        Type = type;
        Latitude = latitude;
        Longitude = longitude;
        ContactName = contactName;
        ContactPhone = contactPhone;
        Notes = notes;
        Status = StopStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }

    public void MarkAsInTransit()
    {
        if (Status != StopStatus.Pending)
            throw new InvalidOperationException($"Cannot mark stop as in transit. Current status: {Status}");

        Status = StopStatus.InTransit;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsArrived()
    {
        if (Status != StopStatus.Pending && Status != StopStatus.InTransit)
            throw new InvalidOperationException($"Cannot mark stop as arrived. Current status: {Status}");

        Status = StopStatus.Arrived;
        ArrivedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        if (Status != StopStatus.Arrived)
            throw new InvalidOperationException($"Cannot mark stop as completed. Current status: {Status}");

        Status = StopStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateAddress(string address, decimal? latitude = null, decimal? longitude = null)
    {
        Address = address;
        Latitude = latitude;
        Longitude = longitude;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Sets the booking ID for this stop.
    /// Used when creating stops before the booking is saved.
    /// </summary>
    internal void SetBookingId(Guid bookingId)
    {
        // Use reflection to set private setter, or make BookingId have internal setter
        // For now, this will be handled by EF Core through navigation property
    }
}

public enum StopType
{
    Pickup,
    Dropoff
}

public enum StopStatus
{
    Pending,
    InTransit,
    Arrived,
    Completed
}
