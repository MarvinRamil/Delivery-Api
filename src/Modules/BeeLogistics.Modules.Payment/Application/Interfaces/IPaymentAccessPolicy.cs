namespace BeeLogistics.Modules.Payment.Application.Interfaces;

/// <summary>
/// The object-level authorization rules for acting on a payment. Kept in one place so the
/// create and link handlers cannot drift apart, mirroring IBookingAccessPolicy in the
/// Bookings module.
/// </summary>
public interface IPaymentAccessPolicy
{
    /// <param name="paymentCustomerId">
    /// Payment.CustomerId (or CreatePaymentDto.CustomerId), which holds the ASP.NET Identity
    /// user id — unlike Booking.CustomerId, which holds a Bookings-module Customer row id.
    /// </param>
    /// <param name="callerUserId">The Identity user id from the JWT subject.</param>
    /// <param name="isElevated">
    /// Whether the caller satisfies the backoffice authorization policy. Resolved in the
    /// presentation layer, where the claims live, and passed down as a plain boolean so the
    /// role vocabulary stays out of the application layer.
    /// </param>
    bool CanActForCustomer(Guid paymentCustomerId, Guid callerUserId, bool isElevated);

    /// <summary>
    /// Whether the caller may attach a payment to <paramref name="bookingId"/>. Requires
    /// ownership of the booking; the caller's ownership of the payment is checked separately
    /// by <see cref="CanActForCustomer"/> so both sides of the link are authorized.
    /// </summary>
    Task<bool> CanLinkToBookingAsync(Guid bookingId, Guid callerUserId, bool isElevated, CancellationToken ct = default);
}
