using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class DriverBookingOffer : Entity
{
    public Guid BookingId { get; private set; }
    public Guid DriverId { get; private set; }
    public DriverOfferStatus Status { get; private set; }
    public DateTime OfferedAt { get; private set; }
    public DateTime? RespondedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public decimal? DistanceKm { get; private set; }
    
    // Fields for the driver-broadcast offer model
    public bool IsFavouriteDriver { get; private set; }
    public decimal? DriverRating { get; private set; } // Average rating
    public int? EstimatedArrivalMinutes { get; private set; }
    
    // Legacy field (deprecated)
    [Obsolete("SequenceNumber is deprecated. This will be removed in a future version.")]
    public int SequenceNumber { get; private set; }

    private DriverBookingOffer() { }

    // Constructor for driver-broadcast offers
    public DriverBookingOffer(
        Guid bookingId,
        Guid driverId,
        DateTime expiresAt,
        decimal? distanceKm = null,
        bool isFavouriteDriver = false,
        decimal? driverRating = null,
        int? estimatedArrivalMinutes = null)
    {
        BookingId = bookingId;
        DriverId = driverId;
        ExpiresAt = expiresAt;
        DistanceKm = distanceKm;
        IsFavouriteDriver = isFavouriteDriver;
        DriverRating = driverRating;
        EstimatedArrivalMinutes = estimatedArrivalMinutes;
        Status = DriverOfferStatus.Pending;
        OfferedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
    
    // Legacy constructor (kept for backward compatibility)
    [Obsolete("Use the new constructor instead. This will be removed in a future version.")]
    public DriverBookingOffer(
        Guid bookingId,
        Guid driverId,
        int sequenceNumber,
        DateTime expiresAt,
        decimal? distanceKm = null)
    {
        BookingId = bookingId;
        DriverId = driverId;
        SequenceNumber = sequenceNumber;
        ExpiresAt = expiresAt;
        DistanceKm = distanceKm;
        Status = DriverOfferStatus.Pending;
        OfferedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void Accept()
    {
        if (Status != DriverOfferStatus.Pending)
            throw new InvalidOperationException("Only pending offers can be accepted");

        if (DateTime.UtcNow > ExpiresAt)
            throw new InvalidOperationException("Offer has expired");

        Status = DriverOfferStatus.Accepted;
        RespondedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reject()
    {
        if (Status != DriverOfferStatus.Pending)
            throw new InvalidOperationException("Only pending offers can be rejected");

        Status = DriverOfferStatus.Rejected;
        RespondedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Expire()
    {
        if (Status != DriverOfferStatus.Pending)
            throw new InvalidOperationException("Only pending offers can be expired");

        Status = DriverOfferStatus.Expired;
        RespondedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt && Status == DriverOfferStatus.Pending;
}

public enum DriverOfferStatus
{
    Pending,
    Accepted,
    Rejected,
    Expired
}
