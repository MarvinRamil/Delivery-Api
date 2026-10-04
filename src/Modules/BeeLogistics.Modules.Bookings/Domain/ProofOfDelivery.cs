using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class ProofOfDelivery : Entity
{
    public Guid BookingId { get; private set; }
    public Guid StopId { get; private set; } // Which delivery stop this POD is for
    public string? ImagePath { get; private set; } // Photo of delivery (optional; client can require)
    public string? SignaturePath { get; private set; } // Customer signature (optional)
    public DateTime DeliveredAt { get; private set; }
    public string? RecipientName { get; private set; }
    public string? Notes { get; private set; }

    // Navigation properties
    public Booking Booking { get; private set; } = null!;
    public DeliveryStop Stop { get; private set; } = null!;

    private ProofOfDelivery() { }

    public ProofOfDelivery(
        Guid bookingId,
        Guid stopId,
        string? imagePath,
        DateTime deliveredAt,
        string? signaturePath = null,
        string? recipientName = null,
        string? notes = null)
    {
        BookingId = bookingId;
        StopId = stopId;
        ImagePath = imagePath;
        SignaturePath = signaturePath;
        DeliveredAt = deliveredAt;
        RecipientName = recipientName;
        Notes = notes;
        CreatedAt = DateTime.UtcNow;
    }

    public void UpdateSignature(string signaturePath)
    {
        SignaturePath = signaturePath;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateNotes(string? notes)
    {
        Notes = notes;
        UpdatedAt = DateTime.UtcNow;
    }
}
