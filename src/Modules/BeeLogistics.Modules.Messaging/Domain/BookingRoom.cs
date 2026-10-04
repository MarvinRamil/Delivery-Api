using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Messaging.Domain;

/// <summary>
/// The link between a booking and its Matrix room, and the record that makes room provisioning
/// idempotent.
/// </summary>
/// <remarks>
/// Provisioning is driven by a MassTransit consumer, so it can be delivered more than once — on
/// broker redelivery, on a retry after a partial failure, or on a replay. The unique index on
/// <see cref="BookingId"/> is what makes a second attempt a no-op rather than a second room.
/// </remarks>
public class BookingRoom : Entity
{
    private BookingRoom() { }

    public BookingRoom(Guid bookingId, string bookingNumber, string roomId, string roomAlias)
    {
        Id = Guid.NewGuid();
        BookingId = bookingId;
        BookingNumber = bookingNumber;
        RoomId = roomId;
        RoomAlias = roomAlias;
        State = BookingRoomState.Active;
    }

    public Guid BookingId { get; private set; }

    /// <summary>Denormalised so the back-office transcript can be found by the number a customer quotes.</summary>
    public string BookingNumber { get; private set; } = string.Empty;

    /// <summary>Synapse's opaque room id (<c>!abc:server</c>), not the alias. Stable for the room's life.</summary>
    public string RoomId { get; private set; } = string.Empty;

    /// <summary>The alias we claimed (<c>#booking-dev-...</c>). Kept for diagnostics and re-resolution.</summary>
    public string RoomAlias { get; private set; } = string.Empty;

    /// <summary>MXID of the driver once one is assigned. Null between booking creation and acceptance.</summary>
    public string? DriverMatrixUserId { get; private set; }

    public string? CustomerMatrixUserId { get; private set; }

    public BookingRoomState State { get; private set; }

    /// <summary>
    /// When the booking reached Completed or Cancelled. Null while it is still running.
    /// </summary>
    /// <remarks>
    /// Recorded here rather than queried from the Bookings module: the freeze job runs on a
    /// schedule over potentially thousands of rooms, and joining across a module boundary on every
    /// pass to ask "has this booking finished yet" would be both slow and a layering violation.
    /// </remarks>
    public DateTime? EndedAt { get; private set; }

    public DateTime? FrozenAt { get; private set; }
    public DateTime? PurgedAt { get; private set; }

    public void RecordCustomer(string matrixUserId) => CustomerMatrixUserId = matrixUserId;

    /// <summary>Idempotent: re-running driver assignment must not look like a change.</summary>
    public void RecordDriver(string matrixUserId)
    {
        if (DriverMatrixUserId == matrixUserId) return;
        DriverMatrixUserId = matrixUserId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Stamps the moment the booking finished. Idempotent, and keeps the FIRST end: a cancellation
    /// re-delivered later must not push the freeze deadline further out each time.
    /// </summary>
    public void MarkBookingEnded(DateTime endedAt)
    {
        if (EndedAt.HasValue) return;
        EndedAt = endedAt;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Room becomes read-only history once the booking has been finished long enough.</summary>
    public void Freeze()
    {
        if (State != BookingRoomState.Active) return;
        State = BookingRoomState.Frozen;
        FrozenAt = DateTime.UtcNow;
        UpdatedAt = FrozenAt;
    }

    /// <summary>
    /// The room is gone from Synapse. The transcript in <c>messaging.room_events</c> is untouched —
    /// purging reclaims homeserver storage, it does not destroy history.
    /// </summary>
    public void MarkPurged()
    {
        State = BookingRoomState.Purged;
        PurgedAt = DateTime.UtcNow;
        UpdatedAt = PurgedAt;
    }
}

public enum BookingRoomState
{
    /// <summary>Both parties can post.</summary>
    Active = 0,

    /// <summary>Readable, but nobody can post. Set a grace period after the booking ends.</summary>
    Frozen = 1,

    /// <summary>Deleted from Synapse past the retention window. The archive survives.</summary>
    Purged = 2,
}
