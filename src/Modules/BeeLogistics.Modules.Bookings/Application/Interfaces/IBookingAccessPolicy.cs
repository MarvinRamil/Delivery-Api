using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

/// <summary>
/// The single object-level authorization rule for a booking: may this caller act on it?
/// Lives in one place so the read, status, delete and cancel handlers cannot drift apart.
/// </summary>
public interface IBookingAccessPolicy
{
    /// <param name="callerUserId">The ASP.NET Identity user id from the JWT subject.</param>
    /// <param name="isElevated">
    /// Whether the caller satisfies the backoffice authorization policy. Resolved in the
    /// presentation layer, where the claims live, and passed down as a plain boolean so the
    /// role vocabulary stays out of the application layer.
    /// </param>
    Task<bool> CanAccessAsync(Booking booking, Guid callerUserId, bool isElevated, CancellationToken ct = default);
}
