using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Consumers;

/// <summary>
/// Posts a booking-status notice into the booking's chat room, so the conversation carries the
/// trip's history rather than only chatter.
/// </summary>
public class BookingChatStatusChangedConsumer : IConsumer<BookingChatStatusChanged>
{
    private readonly IMatrixAppServiceClient _client;
    private readonly IBookingRoomRepository _rooms;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingChatStatusChangedConsumer> _logger;

    public BookingChatStatusChangedConsumer(
        IMatrixAppServiceClient client,
        IBookingRoomRepository rooms,
        IOptions<MatrixOptions> options,
        ILogger<BookingChatStatusChangedConsumer> logger)
    {
        _client = client;
        _rooms = rooms;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingChatStatusChanged> context)
    {
        if (!_options.Enabled) return;

        var ct = context.CancellationToken;
        var msg = context.Message;

        var content = BookingSystemMessageComposer.BookingStatus(msg.Status);
        if (content is null)
        {
            // A status we deliberately do not narrate. Nothing to retry.
            _logger.LogDebug("No chat notice defined for status {Status}", msg.Status);
            return;
        }

        var room = await _rooms.GetByBookingIdAsync(msg.BookingId, ct);
        if (room is null)
        {
            // A status notice is not worth blocking a queue over. Unlike driver assignment - where
            // retrying until the room exists is the point - a missing room here means the booking
            // never got one, and hammering the queue would not conjure it. The transcript loses a
            // line; the archive and the conversation are unaffected.
            _logger.LogWarning("No chat room for booking {BookingId}; dropping {Status} notice",
                msg.BookingId, msg.Status);
            return;
        }

        // Keyed on the status, so a redelivery collapses to the same event instead of repeating
        // the line, while a genuinely new status still posts.
        var txn = BookingSystemMessageComposer.TransactionId(msg.BookingId, "status", msg.Status.ToLowerInvariant());

        await _client.SendMessageAsync(room.RoomId, content, txn, asUserId: null, ct);

        // A finished booking starts the clock the freeze job measures. Recorded here because this
        // is the only place that learns a booking ended; MarkBookingEnded keeps the first end, so
        // a redelivery cannot keep pushing the deadline out.
        if (msg.Status is "Completed" or "Cancelled")
        {
            room.MarkBookingEnded(msg.ChangedAt);
            await _rooms.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Posted {Status} notice to room {RoomId}", msg.Status, room.RoomId);
    }
}
