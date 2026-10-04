using BeeLogistics.Modules.Messaging.Application.DTOs;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IBookingTranscriptReader
{
    Task<BookingTranscriptDto?> ReadAsync(Guid bookingId, int skip, int take, CancellationToken ct = default);
}

/// <summary>
/// Reads a booking's conversation out of bee's own database.
/// </summary>
/// <remarks>
/// Reads the archive, never Synapse. That is the whole point of the design: an admin sees the
/// conversation <b>without joining the room</b>, without being present while it happened, and
/// without the two parties gaining a silent third member. It also keeps working after the
/// retention job purges the room from the homeserver.
/// </remarks>
public sealed class BookingTranscriptReader : IBookingTranscriptReader
{
    private readonly IRoomEventArchive _archive;
    private readonly IBookingRoomRepository _rooms;
    private readonly MatrixOptions _options;

    public BookingTranscriptReader(
        IRoomEventArchive archive,
        IBookingRoomRepository rooms,
        IOptions<MatrixOptions> options)
    {
        _archive = archive;
        _rooms = rooms;
        _options = options.Value;
    }

    public async Task<BookingTranscriptDto?> ReadAsync(Guid bookingId, int skip, int take, CancellationToken ct = default)
    {
        var room = await _rooms.GetByBookingIdAsync(bookingId, ct);
        if (room is null) return null;

        var events = await _archive.GetForBookingAsync(bookingId, skip, take, ct);
        var total = await _archive.CountForBookingAsync(bookingId, ct);

        return new BookingTranscriptDto
        {
            BookingId = bookingId,
            BookingNumber = room.BookingNumber,
            RoomId = room.RoomId,
            RoomState = room.State.ToString(),
            TotalMessages = total,
            Messages = events.Select(e => new TranscriptEntryDto
            {
                EventId = e.MatrixEventId,
                Sender = e.Sender,
                SenderRole = RoleOf(e.Sender, room),
                EventType = e.EventType,
                Body = e.Body,
                // origin_server_ts is Synapse's own timestamp, kept as sent rather than re-derived.
                SentAt = DateTimeOffset.FromUnixTimeMilliseconds(e.OriginServerTs).UtcDateTime,
            }).ToList(),
        };
    }

    /// <summary>
    /// Labels each MXID with its part in this booking, so a reviewer does not have to decode
    /// <c>@bee_u_dev_&lt;guid&gt;</c> to work out who said what.
    /// </summary>
    private string RoleOf(string sender, BookingRoom room)
    {
        if (string.Equals(sender, _options.BotUserId, StringComparison.Ordinal)) return "System";
        if (string.Equals(sender, room.CustomerMatrixUserId, StringComparison.Ordinal)) return "Customer";
        if (string.Equals(sender, room.DriverMatrixUserId, StringComparison.Ordinal)) return "Driver";

        // A previous driver on a reassigned booking, or an admin who joined for a dispute. Worth
        // showing as unknown rather than guessing.
        return "Unknown";
    }
}
