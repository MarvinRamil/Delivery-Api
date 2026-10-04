using BeeLogistics.Modules.Messaging.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IBookingRoomLifecycleService
{
    /// <summary>Makes finished bookings' rooms read-only. Returns how many were frozen.</summary>
    Task<int> FreezeFinishedRoomsAsync(DateTime asOf, CancellationToken ct = default);

    /// <summary>Deletes long-frozen rooms from Synapse. Returns how many were purged.</summary>
    Task<int> PurgeExpiredRoomsAsync(DateTime asOf, CancellationToken ct = default);
}

/// <summary>
/// Ages booking rooms out: active → frozen → purged.
/// </summary>
/// <remarks>
/// The behaviour lives here, in the module, rather than in the Hangfire job wrapper — same split
/// as <c>LocationHistoryRetentionJob</c> and <c>PaymentReconciliationService</c>, so the test
/// project can reach it without referencing Hangfire.
/// </remarks>
public sealed class BookingRoomLifecycleService : IBookingRoomLifecycleService
{
    /// <summary>
    /// Bounded per run so a backlog cannot fan one pass out into thousands of calls to Synapse.
    /// The remainder is picked up next run.
    /// </summary>
    private const int BatchSize = 200;

    private readonly IBookingRoomRepository _rooms;
    private readonly IMatrixAppServiceClient _client;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingRoomLifecycleService> _logger;

    public BookingRoomLifecycleService(
        IBookingRoomRepository rooms,
        IMatrixAppServiceClient client,
        IOptions<MatrixOptions> options,
        ILogger<BookingRoomLifecycleService> logger)
    {
        _rooms = rooms;
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> FreezeFinishedRoomsAsync(DateTime asOf, CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;

        var cutoff = asOf.AddHours(-_options.RoomFreezeDelayHours);
        var due = await _rooms.GetDueForFreezeAsync(cutoff, BatchSize, ct);
        if (due.Count == 0) return 0;

        var frozen = 0;
        foreach (var room in due)
        {
            try
            {
                // events_default 100 with participants at 0 means nobody but the bot can post.
                // The room stays readable — this is retirement, not deletion.
                await _client.SetPowerLevelsAsync(room.RoomId, new
                {
                    users = new Dictionary<string, int> { [_options.BotUserId] = 100 },
                    users_default = 0,
                    events_default = 100,
                    state_default = 100,
                    invite = 100,
                    kick = 100,
                    ban = 100,
                    redact = 100,
                }, ct);

                room.Freeze();
                frozen++;
            }
            catch (Exception ex)
            {
                // One unreachable room must not stop the batch; it stays Active and is retried on
                // the next run, which is exactly the behaviour we want from a sweep.
                _logger.LogWarning(ex, "Could not freeze room {RoomId} for booking {BookingId}",
                    room.RoomId, room.BookingId);
            }
        }

        // Saved once at the end: the room states only matter together, and a per-room save would
        // multiply round trips by the batch size.
        await _rooms.SaveChangesAsync(ct);

        _logger.LogInformation("Froze {Frozen} of {Due} finished booking rooms", frozen, due.Count);
        return frozen;
    }

    public async Task<int> PurgeExpiredRoomsAsync(DateTime asOf, CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;
        if (_options.RoomRetentionDays <= 0) return 0;   // retention disabled

        var cutoff = asOf.AddDays(-_options.RoomRetentionDays);
        var due = await _rooms.GetDueForPurgeAsync(cutoff, BatchSize, ct);
        if (due.Count == 0) return 0;

        var purged = 0;
        foreach (var room in due)
        {
            try
            {
                await _client.PurgeRoomAsync(room.RoomId, ct);
                room.MarkPurged();
                purged++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not purge room {RoomId} for booking {BookingId}",
                    room.RoomId, room.BookingId);
            }
        }

        await _rooms.SaveChangesAsync(ct);

        // Deliberately phrased so nobody reads this as data loss: the transcript is in Postgres.
        _logger.LogInformation(
            "Purged {Purged} of {Due} expired rooms from Synapse; their transcripts remain in messaging.room_events",
            purged, due.Count);
        return purged;
    }
}
