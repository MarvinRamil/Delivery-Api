using System.Text.Json;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using BeeLogistics.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IMatrixTransactionProcessor
{
    /// <summary>Archives every event in a transaction and notifies the other party. Idempotent.</summary>
    Task ProcessAsync(JsonElement transaction, CancellationToken ct = default);
}

/// <summary>
/// Turns an inbound appservice transaction into archive rows and push notifications.
/// </summary>
/// <remarks>
/// This is what makes admin access work. Synapse pushes every event in our namespace here, and the
/// rows written are what the back-office reads — so an admin can review a conversation
/// <b>without joining the room and without having been present while it happened</b>. The archive
/// also outlives Synapse: when the retention job purges a room, these rows remain.
/// </remarks>
public sealed class MatrixTransactionProcessor : IMatrixTransactionProcessor
{
    private readonly IRoomEventArchive _archive;
    private readonly IBookingRoomRepository _rooms;
    private readonly IMatrixIdentityRepository _identities;
    private readonly IPushDispatcher _push;
    private readonly MatrixOptions _options;
    private readonly ILogger<MatrixTransactionProcessor> _logger;

    public MatrixTransactionProcessor(
        IRoomEventArchive archive,
        IBookingRoomRepository rooms,
        IMatrixIdentityRepository identities,
        IPushDispatcher push,
        IOptions<MatrixOptions> options,
        ILogger<MatrixTransactionProcessor> logger)
    {
        _archive = archive;
        _rooms = rooms;
        _identities = identities;
        _push = push;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(JsonElement transaction, CancellationToken ct = default)
    {
        if (!transaction.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            return;

        foreach (var evt in events.EnumerateArray())
        {
            try
            {
                await ProcessEventAsync(evt, ct);
            }
            catch (Exception ex)
            {
                // One malformed or unexpected event must not cost us the rest of the transaction.
                // Synapse retries the whole batch on a non-2xx, so failing the request here would
                // re-deliver the events we already archived — and block the queue behind it.
                _logger.LogError(ex, "Failed to process a Matrix event; continuing with the rest of the transaction");
            }
        }
    }

    private async Task ProcessEventAsync(JsonElement evt, CancellationToken ct)
    {
        var eventId = Get(evt, "event_id");
        var roomId = Get(evt, "room_id");
        var sender = Get(evt, "sender");
        var type = Get(evt, "type");

        if (eventId is null || roomId is null || sender is null || type is null)
        {
            _logger.LogWarning("Skipping a Matrix event missing one of event_id/room_id/sender/type");
            return;
        }

        var content = evt.TryGetProperty("content", out var c) ? c : default;
        var body = content.ValueKind == JsonValueKind.Object && content.TryGetProperty("body", out var b)
            ? b.GetString()
            : null;

        var originServerTs = evt.TryGetProperty("origin_server_ts", out var ts) && ts.TryGetInt64(out var tsv)
            ? tsv
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var room = await _rooms.GetByRoomIdAsync(roomId, ct);

        // BookingId is nullable on purpose: an event for a room we have no mapping for is recorded
        // rather than dropped, because silently discarding it would hide exactly the bug worth
        // knowing about.
        var stored = await _archive.TryAddAsync(new RoomEvent(
            eventId, roomId, room?.BookingId, sender, type, body,
            content.ValueKind == JsonValueKind.Undefined ? "{}" : content.GetRawText(),
            originServerTs), ct);

        if (!stored)
        {
            // A retried transaction. Notifying again would push the same message twice.
            _logger.LogDebug("Event {EventId} already archived; skipping", eventId);
            return;
        }

        if (room is not null && type == "m.room.message" && !IsFromBot(sender))
            await NotifyOtherPartyAsync(room, sender, body, ct);
    }

    private bool IsFromBot(string sender) =>
        string.Equals(sender, _options.BotUserId, StringComparison.Ordinal);

    /// <summary>
    /// Pushes a new message to whichever party did not send it, reusing the existing Expo/FCM
    /// pipeline. No Sygnal push gateway is involved.
    /// </summary>
    private async Task NotifyOtherPartyAsync(BookingRoom room, string senderMxid, string? body, CancellationToken ct)
    {
        var recipientMxid = senderMxid == room.CustomerMatrixUserId
            ? room.DriverMatrixUserId
            : senderMxid == room.DriverMatrixUserId
                ? room.CustomerMatrixUserId
                : null;

        if (recipientMxid is null)
        {
            // Either the driver has not been assigned yet, or the sender is not a room party we
            // recognise. Neither is worth failing the transaction over.
            _logger.LogDebug("No push recipient for a message from {Sender} in {RoomId}", senderMxid, room.RoomId);
            return;
        }

        var identity = await _identities.GetByMatrixUserIdAsync(recipientMxid, ct);
        if (identity is null)
        {
            _logger.LogWarning("No bee identity for {MatrixUserId}; cannot push", recipientMxid);
            return;
        }

        try
        {
            await _push.DispatchAsync(new SendPushRequested
            {
                UserId = identity.BeeUserId,
                Title = $"Booking {room.BookingNumber}",
                Body = Preview(body),
                Data = new Dictionary<string, string>
                {
                    ["type"] = "booking_chat",
                    ["bookingId"] = room.BookingId.ToString(),
                    ["roomId"] = room.RoomId,
                },
            }, source: "matrix-chat", ct);
        }
        catch (Exception ex)
        {
            // A push failure must never fail the transaction: the message is already archived and
            // already in the room, and a retry would duplicate the archive check for nothing.
            _logger.LogWarning(ex, "Push for a chat message in {RoomId} failed", room.RoomId);
        }
    }

    /// <summary>
    /// Truncates the notification body. A push travels through Expo/FCM and can surface on a lock
    /// screen, so it carries a preview rather than the whole message.
    /// </summary>
    private static string Preview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "New message";
        var trimmed = body.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..117] + "…";
    }

    private static string? Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
