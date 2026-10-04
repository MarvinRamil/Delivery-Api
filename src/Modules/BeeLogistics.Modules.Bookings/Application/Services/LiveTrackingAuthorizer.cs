using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Grants live-location access to a driver only when the requesting customer has an
/// active booking served by that driver. Prevents cross-customer driver tracking (IDOR).
/// </summary>
public sealed class LiveTrackingAuthorizer : ILiveTrackingAuthorizer
{
    // Statuses where a driver is assigned and the delivery is in progress, so the
    // customer legitimately needs the driver's live location. Pending has no driver;
    // Completed/Cancelled are terminal.
    private static readonly BookingStatus[] TrackableStatuses =
    {
        BookingStatus.Confirmed,
        BookingStatus.DriverAssigned,
        BookingStatus.PickedUp,
        BookingStatus.InTransit,
    };

    private readonly IBookingRepository _bookings;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;

    public LiveTrackingAuthorizer(IBookingRepository bookings, ICustomerIdentityResolver customerIdentityResolver)
    {
        _bookings = bookings;
        _customerIdentityResolver = customerIdentityResolver;
    }

    public async Task<bool> CanTrackDriverAsync(Guid requesterUserId, Guid driverId, CancellationToken ct = default)
    {
        if (requesterUserId == Guid.Empty || driverId == Guid.Empty)
            return false;

        // Booking.CustomerId is the Bookings-module Customer entity Id, NOT the Identity
        // UserId the JWT carries - the two are different tables. Resolve the requester to
        // their Customer row before comparing, or every customer is denied.
        var requesterCustomerId = await _customerIdentityResolver.ResolveBookingCustomerIdAsync(
            requesterUserId.ToString(), ct);

        // GetByDriverIdAsync returns every booking assigned to the driver, cancelled ones
        // included - it is the driver's history feed, not a live-work query. The
        // TrackableStatuses filter below is what keeps a cancelled or completed booking from
        // granting location access, so it must stay even if the repository narrows again later.
        var driverBookings = await _bookings.GetByDriverIdAsync(driverId, ct);
        return driverBookings.Any(b =>
            TrackableStatuses.Contains(b.Status) &&
            // The resolved Customer Id is the normal path; the raw user id still matches the
            // legacy rows created through the CustomerId-supplied booking path.
            (b.CustomerId == requesterCustomerId || b.CustomerId == requesterUserId));
    }
}
