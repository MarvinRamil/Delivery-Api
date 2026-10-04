namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Resolves the ASP.NET Identity UserId for a Bookings-module Customer.
/// Required because Customer.Id ≠ Identity UserId, and SignalR groups
/// connections by Identity UserId (JWT sub claim).
/// </summary>
public interface ICustomerIdentityResolver
{
    /// <summary>
    /// Given a Bookings-module CustomerId, returns the Identity UserId
    /// that SignalR uses for notification routing.
    /// Returns null if resolution fails (caller should fall back gracefully).
    /// </summary>
    Task<string?> ResolveIdentityUserIdAsync(Guid bookingCustomerId, CancellationToken ct = default);

    /// <summary>
    /// The reverse mapping: given an Identity UserId (the JWT sub claim), returns the
    /// Bookings-module CustomerId stored on <c>Booking.CustomerId</c>.
    /// Returns null if the user has no email or no matching Customer row.
    /// </summary>
    Task<Guid?> ResolveBookingCustomerIdAsync(string identityUserId, CancellationToken ct = default);
}
