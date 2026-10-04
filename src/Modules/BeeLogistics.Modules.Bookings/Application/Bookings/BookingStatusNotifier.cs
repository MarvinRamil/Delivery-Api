using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// The one place that decides who hears about a booking: the owning customer and, normally,
/// the assigned driver. Every booking notification goes through here.
///
/// NotificationHub is [Authorize]d but not scoped to a booking, so <c>SendToAllAsync</c> on a
/// booking event reaches every signed-in user on the platform rather than the two parties —
/// which is exactly what cancellation used to do. Routing the audience through a single helper
/// is what keeps that from coming back at the next call site.
/// </summary>
internal static class BookingStatusNotifier
{
    /// <summary>
    /// Sends <paramref name="payload"/> to the booking's customer and, when
    /// <paramref name="includeDriver"/> is set, its assigned driver.
    /// </summary>
    /// <remarks>
    /// The payload is built by the caller and passed through untouched — the call sites emit
    /// genuinely different shapes, and keeping construction local means this refactor cannot
    /// alter what goes out on the wire.
    /// </remarks>
    /// <param name="chatPublisher">
    /// Mirrors a booking-status change into the booking's Matrix room (#78). Required rather than
    /// optional so a new call site has to decide consciously; pass null where the room is already
    /// being told by another path, and say why.
    ///
    /// Published directly rather than through the outbox because every caller reaches here after
    /// its SaveChanges, and an outbox message staged after the last save is silently discarded.
    /// See <see cref="BookingChatStatusChanged"/>.
    /// </param>
    internal static async Task PublishToBookingPartiesAsync(
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        IDirectBusPublisher? chatPublisher,
        Booking booking,
        string method,
        object payload,
        bool includeDriver = true,
        CancellationToken ct = default)
    {
        // Resolve the Identity UserId for the customer (Customer.Id ≠ Identity UserId)
        var customerNotifyId = await customerIdentityResolver.ResolveIdentityUserIdAsync(booking.CustomerId, ct)
            ?? booking.CustomerId.ToString(); // fallback

        await notificationService.SendToUserAsync(customerNotifyId, method, payload);

        if (includeDriver && booking.SelectedDriverId.HasValue)
        {
            await notificationService.SendToUserAsync(booking.SelectedDriverId.Value.ToString(), method, payload);
        }

        // Only booking-status changes reach the chat room. Stop-level events are far noisier and
        // belong on the tracking map, not in a conversation between two people.
        if (chatPublisher is not null && method == "BookingStatusChanged")
        {
            await chatPublisher.PublishAsync(new BookingChatStatusChanged
            {
                BookingId = booking.Id,
                Status = booking.Status.ToString(),
                PreviousStatus = null,
                ChangedAt = DateTime.UtcNow,
            }, ct);
        }
    }
}
