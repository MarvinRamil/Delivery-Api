using BeeLogistics.Modules.Messaging.Domain;

namespace BeeLogistics.Modules.Messaging.Application.Interfaces;

public interface IRoomEventArchive
{
    /// <summary>
    /// Records an event, or reports that it was already recorded.
    /// </summary>
    /// <returns>True when this call stored the event; false when it was a duplicate.</returns>
    /// <remarks>
    /// Synapse retries a transaction until it gets a 2xx, so the same event genuinely arrives more
    /// than once. The unique index on the Matrix event id is the idempotency mechanism, and this
    /// turning a duplicate-key error into <c>false</c> is what lets the endpoint answer 200
    /// unconditionally.
    /// </remarks>
    Task<bool> TryAddAsync(RoomEvent roomEvent, CancellationToken ct = default);

    /// <summary>The transcript for one booking, oldest first.</summary>
    Task<IReadOnlyList<RoomEvent>> GetForBookingAsync(Guid bookingId, int skip, int take, CancellationToken ct = default);

    Task<int> CountForBookingAsync(Guid bookingId, CancellationToken ct = default);
}
