namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Authorizes whether a user may subscribe to a driver's live location stream.
/// Implemented in the Bookings module, which is the only place that knows the
/// customer↔driver booking relationship. Lives in Shared.Contracts so the Map
/// module's LocationHub can depend on it without referencing Bookings directly.
/// </summary>
public interface ILiveTrackingAuthorizer
{
    /// <summary>
    /// True when <paramref name="requesterUserId"/> has an active (in-progress) booking
    /// served by <paramref name="driverId"/>, and may therefore watch that driver's location.
    /// </summary>
    Task<bool> CanTrackDriverAsync(Guid requesterUserId, Guid driverId, CancellationToken ct = default);
}
