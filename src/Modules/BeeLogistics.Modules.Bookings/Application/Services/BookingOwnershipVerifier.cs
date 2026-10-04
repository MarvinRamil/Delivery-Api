using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Customer-ownership check for a booking, exposed to other modules through
/// <see cref="IBookingOwnershipVerifier"/>.
///
/// Deliberately narrower than <see cref="BookingAccessPolicy"/>: it does NOT grant the assigned
/// driver. Attaching a payment to a booking is a customer action, and the driver is the party
/// whose earnings are computed from that payment's amount — so the driver must not be able to
/// choose which payment their booking settles against.
/// </summary>
public sealed class BookingOwnershipVerifier : IBookingOwnershipVerifier
{
    private readonly IBookingRepository _bookings;
    private readonly ICustomerIdentityResolver _customerIdentityResolver;

    public BookingOwnershipVerifier(
        IBookingRepository bookings,
        ICustomerIdentityResolver customerIdentityResolver)
    {
        _bookings = bookings;
        _customerIdentityResolver = customerIdentityResolver;
    }

    public async Task<bool> IsOwnedByCustomerAsync(Guid bookingId, Guid callerUserId, CancellationToken ct = default)
    {
        if (bookingId == Guid.Empty || callerUserId == Guid.Empty)
            return false;

        var booking = await _bookings.GetByIdAsync(bookingId, ct);
        if (booking == null)
            return false;

        // Booking.CustomerId is the Bookings-module Customer entity Id, NOT the Identity
        // UserId the JWT carries - the two are different tables. Resolve the caller to their
        // Customer row before comparing, or every real customer is denied.
        var callerCustomerId = await _customerIdentityResolver.ResolveBookingCustomerIdAsync(
            callerUserId.ToString(), ct);

        if (callerCustomerId.HasValue && booking.CustomerId == callerCustomerId.Value)
            return true;

        // Legacy rows created through the CustomerId-supplied booking path store the raw
        // Identity user id in CustomerId. Same fallback BookingAccessPolicy and
        // LiveTrackingAuthorizer carry - dropping it would deny those customers their own bookings.
        return booking.CustomerId == callerUserId;
    }
}
