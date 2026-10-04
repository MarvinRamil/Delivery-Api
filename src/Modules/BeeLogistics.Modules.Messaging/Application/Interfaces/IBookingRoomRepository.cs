using BeeLogistics.Modules.Messaging.Domain;

namespace BeeLogistics.Modules.Messaging.Application.Interfaces;

public interface IBookingRoomRepository
{
    Task<BookingRoom?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<BookingRoom?> GetByRoomIdAsync(string roomId, CancellationToken ct = default);

    /// <summary>Adds a room mapping, or returns the existing one if a redelivery beat us to it.</summary>
    Task<BookingRoom> AddOrGetAsync(BookingRoom room, CancellationToken ct = default);

    /// <summary>
    /// Active rooms whose booking ended before <paramref name="endedBefore"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<BookingRoom>> GetDueForFreezeAsync(DateTime endedBefore, int limit, CancellationToken ct = default);

    /// <summary>
    /// Frozen rooms frozen before <paramref name="frozenBefore"/>, oldest first. These are purged
    /// from Synapse; their transcripts in room_events are untouched.
    /// </summary>
    Task<IReadOnlyList<BookingRoom>> GetDueForPurgeAsync(DateTime frozenBefore, int limit, CancellationToken ct = default);

    /// <summary>
    /// Of the given booking ids, those that already have a room. Backs the backstop sweep, which
    /// needs the complement.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetExistingBookingIdsAsync(IReadOnlyList<Guid> bookingIds, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
