namespace BeeLogistics.Modules.Messaging.Application.Interfaces;

/// <summary>
/// The Matrix Application Service API, as much of it as booking chat needs.
/// </summary>
/// <remarks>
/// There is no Matrix SDK behind this and none is wanted. An application service talks to the
/// homeserver over the ordinary Client-Server API with two additions: the <c>as_token</c> as a
/// bearer credential, and a <c>?user_id=</c> query parameter to act as one of its namespaced
/// users. That is a typed HttpClient, not a dependency.
/// </remarks>
public interface IMatrixAppServiceClient
{
    /// <summary>
    /// Ensures a namespaced Matrix account exists, returning its MXID. Idempotent: an account that
    /// already exists is reported as success, because provisioning runs from a retryable consumer.
    /// </summary>
    Task<string> EnsureUserAsync(string localpart, string? displayName, CancellationToken ct = default);

    /// <summary>
    /// Mints a client access token for one of our users via <c>m.login.application_service</c>.
    /// </summary>
    /// <param name="deviceId">
    /// Re-uses an existing device when supplied, so a re-login does not add a device per app
    /// launch. Synapse allocates one when null.
    /// </param>
    Task<MatrixSession> LoginAsUserAsync(string localpart, string? deviceId, string? deviceDisplayName, CancellationToken ct = default);

    /// <summary>
    /// Creates a room with the given alias localpart, or adopts the existing one if the alias is
    /// already claimed.
    /// </summary>
    /// <remarks>
    /// The alias is the idempotency key. A redelivered provisioning message hits
    /// <c>M_ROOM_IN_USE</c>, and adopting is the correct response — the room it names is by
    /// definition this booking's room, because the environment prefix makes the alias unique to
    /// this environment.
    /// </remarks>
    Task<RoomCreationResult> CreateOrAdoptRoomAsync(CreateRoomRequest request, CancellationToken ct = default);

    /// <summary>Invites a user to a room, acting as the bot. Already-invited and already-joined are successes.</summary>
    Task InviteAsync(string roomId, string matrixUserId, CancellationToken ct = default);

    /// <summary>
    /// Joins a room while masquerading as one of our users. Appservice users never see an invite
    /// UI, so we accept on their behalf at provisioning time. Already-joined is a success.
    /// </summary>
    Task JoinAsUserAsync(string roomId, string matrixUserId, CancellationToken ct = default);

    /// <summary>
    /// Sends a message event. <paramref name="transactionId"/> must be stable for a given logical
    /// send: Matrix de-duplicates on it, which is what makes a retry safe.
    /// </summary>
    Task<string> SendMessageAsync(
        string roomId,
        object content,
        string transactionId,
        string? asUserId = null,
        CancellationToken ct = default);

    /// <summary>Resolves a room alias to its room id, or null when unclaimed.</summary>
    Task<string?> ResolveAliasAsync(string alias, CancellationToken ct = default);

    /// <summary>Replaces a room's power levels — used to freeze a finished booking's room.</summary>
    Task SetPowerLevelsAsync(string roomId, object content, CancellationToken ct = default);

    /// <summary>
    /// Deletes a room from Synapse via the admin API, reclaiming its storage.
    /// </summary>
    /// <remarks>
    /// Uses <c>Matrix:AdminToken</c>, not the appservice token — the appservice has no admin rights
    /// and should not. Destroys the room on the homeserver only; the transcript in
    /// <c>messaging.room_events</c> is untouched, which is what lets a dispute be reviewed months
    /// later.
    /// </remarks>
    Task PurgeRoomAsync(string roomId, CancellationToken ct = default);
}

/// <summary>What a client needs to talk to Synapse directly. Never persisted server-side.</summary>
public sealed record MatrixSession(string UserId, string AccessToken, string DeviceId);

public sealed record RoomCreationResult(string RoomId, bool Adopted);

public sealed record CreateRoomRequest(
    string AliasLocalpart,
    string Name,
    string? Topic,
    string BotUserId,
    IReadOnlyList<string> InviteUserIds,
    IReadOnlyDictionary<string, object>? BookingState);
