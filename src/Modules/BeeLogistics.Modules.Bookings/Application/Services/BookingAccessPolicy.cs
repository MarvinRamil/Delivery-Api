using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Grants access to a booking only to the owning customer, the assigned driver, or an
/// elevated (backoffice) caller. Closes the IDOR on the by-id read, status and delete
/// endpoints, which previously acted on any booking for any authenticated caller.
/// </summary>
public sealed class BookingAccessPolicy : IBookingAccessPolicy
{
    private readonly ICustomerIdentityResolver _customerIdentityResolver;

    public BookingAccessPolicy(ICustomerIdentityResolver customerIdentityResolver)
        => _customerIdentityResolver = customerIdentityResolver;

    public async Task<bool> CanAccessAsync(Booking booking, Guid callerUserId, bool isElevated, CancellationToken ct = default)
    {
        if (isElevated)
            return true;

        if (booking == null || callerUserId == Guid.Empty)
            return false;

        // Booking.CustomerId is the Bookings-module Customer entity Id, NOT the Identity
        // UserId the JWT carries - the two are different tables. Resolve the caller to their
        // Customer row before comparing, or every real customer is denied.
        var callerCustomerId = await _customerIdentityResolver.ResolveBookingCustomerIdAsync(
            callerUserId.ToString(), ct);

        if (callerCustomerId.HasValue && booking.CustomerId == callerCustomerId.Value)
            return true;

        // Legacy rows created through the CustomerId-supplied booking path store the raw
        // Identity user id in CustomerId. Same fallback LiveTrackingAuthorizer and the
        // cancel handler carry - dropping it would deny those customers their own bookings.
        if (booking.CustomerId == callerUserId)
            return true;

        return booking.SelectedDriverId == callerUserId;
    }
}
