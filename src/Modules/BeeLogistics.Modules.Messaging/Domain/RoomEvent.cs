using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Messaging.Domain;

/// <summary>
/// One Matrix event, mirrored into bee's own database as it happens.
/// </summary>
/// <remarks>
/// <para>
/// This table is what lets an admin read a booking conversation <b>without joining the room and
/// without being present while it happens</b>. The appservice receives every event in its
/// namespace and writes it here; the back-office reads this, never Synapse.
/// </para>
/// <para>
/// It also outlives Synapse: when the retention job purges a room, these rows remain, so a dispute
/// raised months later still has a transcript.
/// </para>
/// <para>
/// <see cref="MatrixEventId"/> is unique, which is the whole idempotency story for the transaction
/// endpoint. Synapse retries a transaction until it gets a 2xx, so the same event genuinely does
/// arrive more than once, and the endpoint can answer 200 unconditionally because a duplicate
/// insert is a no-op rather than a second copy.
/// </para>
/// </remarks>
public class RoomEvent : Entity
{
    private RoomEvent() { }

    public RoomEvent(
        string matrixEventId,
        string roomId,
        Guid? bookingId,
        string sender,
        string eventType,
        string? body,
        string content,
        long originServerTs)
    {
        Id = Guid.NewGuid();
        MatrixEventId = matrixEventId;
        RoomId = roomId;
        BookingId = bookingId;
        Sender = sender;
        EventType = eventType;
        Body = body;
        Content = content;
        OriginServerTs = originServerTs;
    }

    /// <summary>Synapse's event id (<c>$abc...</c>). Unique — the idempotency key.</summary>
    public string MatrixEventId { get; private set; } = string.Empty;

    public string RoomId { get; private set; } = string.Empty;

    /// <summary>
    /// Resolved from <see cref="RoomId"/> at write time. Nullable because an event can arrive for a
    /// room we have no mapping for — that should be recorded, not dropped, since silently
    /// discarding it would hide exactly the bug worth knowing about.
    /// </summary>
    public Guid? BookingId { get; private set; }

    /// <summary>MXID of the sender.</summary>
    public string Sender { get; private set; } = string.Empty;

    /// <summary>Matrix event type, e.g. <c>m.room.message</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>
    /// The plain-text body, lifted out of the content for searching and for the back-office list
    /// view. Null for events that have no body (membership changes, state events).
    /// </summary>
    public string? Body { get; private set; }

    /// <summary>The full event content as JSON, so nothing is lost to the projection above.</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>Synapse's own timestamp, milliseconds since epoch. Kept as sent, not re-derived.</summary>
    public long OriginServerTs { get; private set; }
}
