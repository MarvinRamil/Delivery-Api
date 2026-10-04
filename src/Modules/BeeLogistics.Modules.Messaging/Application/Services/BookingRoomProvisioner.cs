using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Services;

public interface IBookingRoomProvisioner
{
    /// <summary>
    /// Ensures a booking has a Matrix room with its customer in it. Idempotent.
    /// </summary>
    Task<BookingRoom> EnsureRoomAsync(
        Guid bookingId,
        string bookingNumber,
        string customerBeeUserId,
        string? customerDisplayName,
        string? pickup,
        string? dropoff,
        CancellationToken ct = default);
}

/// <summary>
/// Creates a booking's chat room and seats the customer in it.
/// </summary>
/// <remarks>
/// Every step is idempotent because this runs from a MassTransit consumer that will be retried on
/// any transient failure, and because a partial failure must be resumable — if the room was created
/// but the join failed, the retry has to finish the job rather than start a second room.
/// </remarks>
public sealed class BookingRoomProvisioner : IBookingRoomProvisioner
{
    private readonly IMatrixAppServiceClient _client;
    private readonly IMatrixUserProvisioner _users;
    private readonly IBookingRoomRepository _rooms;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingRoomProvisioner> _logger;

    public BookingRoomProvisioner(
        IMatrixAppServiceClient client,
        IMatrixUserProvisioner users,
        IBookingRoomRepository rooms,
        IOptions<MatrixOptions> options,
        ILogger<BookingRoomProvisioner> logger)
    {
        _client = client;
        _users = users;
        _rooms = rooms;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BookingRoom> EnsureRoomAsync(
        Guid bookingId,
        string bookingNumber,
        string customerBeeUserId,
        string? customerDisplayName,
        string? pickup,
        string? dropoff,
        CancellationToken ct = default)
    {
        var customerMxid = await _users.EnsureAsync(customerBeeUserId, customerDisplayName, ct);

        var existing = await _rooms.GetByBookingIdAsync(bookingId, ct);
        if (existing is not null)
        {
            // Already provisioned. Still re-assert the join: the common partial failure is a room
            // that exists with nobody in it, and returning early would leave it that way forever.
            await _client.JoinAsUserAsync(existing.RoomId, customerMxid, ct);
            _logger.LogDebug("Booking {BookingId} already has room {RoomId}", bookingId, existing.RoomId);
            return existing;
        }

        var aliasLocalpart = $"{_options.RoomAliasPrefix}{bookingNumber.ToLowerInvariant()}";

        var result = await _client.CreateOrAdoptRoomAsync(new CreateRoomRequest(
            AliasLocalpart: aliasLocalpart,
            Name: bookingNumber,
            Topic: BuildTopic(pickup, dropoff),
            BotUserId: _options.BotUserId,
            InviteUserIds: new[] { customerMxid },
            BookingState: new Dictionary<string, object>
            {
                ["bookingId"] = bookingId.ToString(),
                ["bookingNumber"] = bookingNumber,
                ["environment"] = _options.EnvironmentPrefix,
            }), ct);

        // Appservice users have no client to accept an invite, so we accept for them.
        await _client.JoinAsUserAsync(result.RoomId, customerMxid, ct);

        var room = new BookingRoom(bookingId, bookingNumber, result.RoomId, $"#{aliasLocalpart}:{_options.ServerName}");
        room.RecordCustomer(customerMxid);

        // AddOrGet, not Add: two deliveries can race past the GetByBookingId check above, and the
        // unique index is what actually decides. The loser takes the winner's row.
        var stored = await _rooms.AddOrGetAsync(room, ct);

        _logger.LogInformation(
            "Booking {BookingId} ({BookingNumber}) -> room {RoomId} (adopted: {Adopted})",
            bookingId, bookingNumber, stored.RoomId, result.Adopted);

        return stored;
    }

    private static string? BuildTopic(string? pickup, string? dropoff)
    {
        if (string.IsNullOrWhiteSpace(pickup) && string.IsNullOrWhiteSpace(dropoff)) return null;
        return $"{pickup ?? "?"} → {dropoff ?? "?"}";
    }
}
