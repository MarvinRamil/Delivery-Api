namespace BeeLogistics.Modules.Messaging.Application.DTOs;

/// <summary>
/// Everything a client needs to talk to Synapse directly, from here on without the backend.
/// </summary>
/// <remarks>
/// The access token is minted per request and never persisted server-side — see
/// <c>MatrixDevice</c>. The client stores it, and stores <see cref="DeviceId"/> so the next login
/// re-uses the device instead of accumulating one per app launch.
/// </remarks>
public sealed record MatrixSessionDto
{
    /// <summary>Public homeserver URL — the one clients can actually resolve.</summary>
    public required string HomeserverUrl { get; init; }
    public required string UserId { get; init; }
    public required string AccessToken { get; init; }
    public required string DeviceId { get; init; }
}

public sealed record BookingTranscriptDto
{
    public required Guid BookingId { get; init; }
    public required string BookingNumber { get; init; }
    public required string RoomId { get; init; }

    /// <summary>Active, Frozen or Purged. A purged room still has a readable transcript here.</summary>
    public required string RoomState { get; init; }

    public required int TotalMessages { get; init; }
    public required IReadOnlyList<TranscriptEntryDto> Messages { get; init; }
}

public sealed record TranscriptEntryDto
{
    public required string EventId { get; init; }
    public required string Sender { get; init; }

    /// <summary>Who the sender was in this booking: Customer, Driver, System, or Unknown.</summary>
    public required string SenderRole { get; init; }

    public required string EventType { get; init; }
    public string? Body { get; init; }
    public required DateTime SentAt { get; init; }
}
