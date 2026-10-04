namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

/// <summary>
/// Details a driver needs to decide on a new booking offer. Passed by the broadcaster
/// (which already has the booking loaded) so the notifier does no extra DB work.
/// </summary>
public record NewOfferNotification(
    Guid OfferId,
    Guid BookingId,
    string BookingNumber,
    string? PickupAddress,
    string? DropoffAddress,
    decimal? EstimatedFare,
    decimal? DistanceKm,
    DateTime ExpiresAt);

/// <summary>
/// Notifies a driver of a new booking offer over SignalR (when the app is open) and
/// push (when it's backgrounded), so offers no longer depend on the app polling.
/// Implementations must never throw — a notification failure must not abort offer creation.
/// </summary>
public interface IDriverOfferNotifier
{
    Task NotifyNewOfferAsync(Guid driverId, NewOfferNotification offer, CancellationToken ct = default);
}
