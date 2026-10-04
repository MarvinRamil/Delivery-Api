using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

internal static class BookingStopStatusFlow
{
    internal static void ApplyDerivedBookingStatus(Booking booking)
    {
        // Don't modify cancelled or completed bookings
        if (booking.Status == BookingStatus.Cancelled || booking.Status == BookingStatus.Completed)
            return;

        var pickupStop = booking.Stops
            .OrderBy(s => s.Sequence)
            .FirstOrDefault(s => s.Type == StopType.Pickup);

        // Only update booking status when pickup is completed
        if (pickupStop?.Status != StopStatus.Completed)
            return;

        var hasIncompleteDropoffs = booking.Stops.Any(s => s.Type == StopType.Dropoff && s.Status != StopStatus.Completed);

        // If there are incomplete dropoffs, advance status based on current status
        if (hasIncompleteDropoffs)
        {
            // Only advance status forward, never backward
            if (booking.Status == BookingStatus.DriverAssigned || booking.Status == BookingStatus.Confirmed)
            {
                booking.MarkPickedUp();
            }
            else if (booking.Status == BookingStatus.PickedUp)
            {
                booking.MarkInTransit();
            }
            // If already InTransit or later, preserve the status (don't change it)
        }
        else
        {
            // All stops completed - this should be handled by CompleteStopCommandHandler
            // But if we're here, just ensure status is at least PickedUp
            if (booking.Status == BookingStatus.DriverAssigned || booking.Status == BookingStatus.Confirmed)
            {
                booking.MarkPickedUp();
            }
            // Preserve InTransit or later statuses
        }
    }

    internal static async Task PublishStopAndBookingStatusAsync(
        INotificationService notificationService,
        ICustomerIdentityResolver customerIdentityResolver,
        IDirectBusPublisher? chatPublisher,
        Booking booking,
        DeliveryStop stop,
        BookingStatus? previousBookingStatus = null)
    {
        var stopPayload = new
        {
            bookingId = booking.Id,
            bookingNumber = booking.BookingNumber,
            stopId = stop.Id,
            stopSequence = stop.Sequence,
            stopType = stop.Type.ToString(),
            stopStatus = stop.Status.ToString(),
            arrivedAt = stop.ArrivedAt,
            completedAt = stop.CompletedAt,
            completedStops = booking.Stops.Count(s => s.Status == StopStatus.Completed),
            totalStops = booking.Stops.Count
        };

        // null: per-stop progress is map detail, not conversation. Only the booking-level status
        // change below is worth a line in the room.
        await BookingStatusNotifier.PublishToBookingPartiesAsync(
            notificationService, customerIdentityResolver, null, booking, "BookingStopStatusChanged", stopPayload);

        if (previousBookingStatus.HasValue && previousBookingStatus.Value != booking.Status)
        {
            var bookingPayload = new
            {
                bookingId = booking.Id,
                bookingNumber = booking.BookingNumber,
                previousStatus = previousBookingStatus.Value.ToString(),
                currentStatus = booking.Status.ToString(),
                updatedAt = DateTime.UtcNow,
                completedStops = booking.Stops.Count(s => s.Status == StopStatus.Completed),
                totalStops = booking.Stops.Count
            };

            await BookingStatusNotifier.PublishToBookingPartiesAsync(
                notificationService, customerIdentityResolver, chatPublisher, booking, "BookingStatusChanged", bookingPayload);
        }
    }
}
