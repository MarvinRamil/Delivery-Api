using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Notification.Application.Services;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

/// <summary>
/// Sends a "NewOffer" to a driver via SignalR + Expo push using the shared
/// <see cref="CombinedNotificationService"/>. The driver app subscribes to the
/// "NewOffer" SignalR event and treats push taps the same way, so a created offer
/// reaches the driver immediately instead of waiting for the next poll.
/// </summary>
public class DriverOfferNotifier : IDriverOfferNotifier
{
    private const string DriverAppType = "driver";
    private readonly CombinedNotificationService _notifications;
    private readonly ILogger<DriverOfferNotifier> _logger;

    public DriverOfferNotifier(CombinedNotificationService notifications, ILogger<DriverOfferNotifier> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public async Task NotifyNewOfferAsync(Guid driverId, NewOfferNotification offer, CancellationToken ct = default)
    {
        try
        {
            var data = new
            {
                type = "NewOffer",
                offerId = offer.OfferId,
                bookingId = offer.BookingId,
                bookingNumber = offer.BookingNumber,
                pickupAddress = offer.PickupAddress,
                dropoffAddress = offer.DropoffAddress,
                estimatedFare = offer.EstimatedFare,
                distanceKm = offer.DistanceKm,
                expiresAt = offer.ExpiresAt
            };

            await _notifications.SendToUserAsync(
                driverId.ToString(),
                DriverAppType,
                method: "NewOffer",
                signalRData: data,
                pushTitle: "New booking offer",
                pushBody: BuildBody(offer),
                pushData: data,
                ct: ct);
        }
        catch (Exception ex)
        {
            // Never let a notification failure break offer creation; the driver can still
            // discover the offer via the polling fallback.
            _logger.LogWarning(ex, "Failed to notify driver {DriverId} of new offer {OfferId}", driverId, offer.OfferId);
        }
    }

    private static string BuildBody(NewOfferNotification o)
    {
        var route = !string.IsNullOrWhiteSpace(o.PickupAddress) && !string.IsNullOrWhiteSpace(o.DropoffAddress)
            ? $"{o.PickupAddress} → {o.DropoffAddress}"
            : o.PickupAddress ?? "New delivery request";
        return o.EstimatedFare.HasValue ? $"{route} · ₱{o.EstimatedFare.Value:N0}" : route;
    }
}
