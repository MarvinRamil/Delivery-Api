namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Answers whether a caller owns a booking, without exposing the Bookings domain.
/// Implemented in the Bookings module, which is the only place that knows both the
/// customer↔booking relationship and the Identity-user↔Customer-row mapping. Lives in
/// Shared.Contracts so the Payment module can authorize a payment→booking link without
/// referencing Bookings directly (same arrangement as <see cref="ILiveTrackingAuthorizer"/>).
/// </summary>
public interface IBookingOwnershipVerifier
{
    /// <param name="callerUserId">The ASP.NET Identity user id from the JWT subject.</param>
    /// <returns>
    /// True only when the caller is the booking's customer. Returns false for a booking that
    /// does not exist, so this cannot be used to probe which booking ids are real.
    /// </returns>
    Task<bool> IsOwnedByCustomerAsync(Guid bookingId, Guid callerUserId, CancellationToken ct = default);
}
