using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Helper
internal static class DriverOfferMapper
{
    public static DriverBookingOfferDto ToDto(DriverBookingOffer o) => new(
        o.Id,
        o.BookingId,
        o.DriverId,
        Guid.Empty, // Tenancy removed in #43; kept on the wire for older driver builds
        o.Status,
        o.OfferedAt,
        o.RespondedAt,
        o.ExpiresAt,
        o.SequenceNumber,
        o.DistanceKm
    );
}
