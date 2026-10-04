using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Application.Consumers;

/// <summary>
/// Seats the assigned driver in the booking's chat room and announces it.
/// </summary>
/// <remarks>
/// Does not inject <c>IPublishEndpoint</c> — see <see cref="BookingChatRoomRequestedConsumer"/>
/// and the convention test that enforces it.
/// </remarks>
public class BookingChatDriverAssignedConsumer : IConsumer<BookingChatDriverAssigned>
{
    private readonly IMatrixAppServiceClient _client;
    private readonly IMatrixUserProvisioner _users;
    private readonly IBookingRoomRepository _rooms;
    private readonly MatrixOptions _options;
    private readonly ILogger<BookingChatDriverAssignedConsumer> _logger;

    public BookingChatDriverAssignedConsumer(
        IMatrixAppServiceClient client,
        IMatrixUserProvisioner users,
        IBookingRoomRepository rooms,
        IOptions<MatrixOptions> options,
        ILogger<BookingChatDriverAssignedConsumer> logger)
    {
        _client = client;
        _users = users;
        _rooms = rooms;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingChatDriverAssigned> context)
    {
        if (!_options.Enabled) return;

        var ct = context.CancellationToken;
        var msg = context.Message;

        var room = await _rooms.GetByBookingIdAsync(msg.BookingId, ct);
        if (room is null)
        {
            // Ordering is not guaranteed across queues: a driver can accept before the room's own
            // provisioning message has been consumed. Throwing puts this on the retry schedule,
            // by which time the room normally exists. Creating one here instead would race the
            // provisioner and risk two rooms for one booking.
            throw new InvalidOperationException(
                $"Booking {msg.BookingId} has no chat room yet; retrying until provisioning catches up.");
        }

        var driverMxid = await _users.EnsureAsync(msg.DriverId.ToString(), msg.DriverName, ct);

        // Invite as the bot, then accept on the driver's behalf — an appservice user has no client
        // to tap "accept". Both calls treat "already done" as success, so a retry is harmless.
        await _client.InviteAsync(room.RoomId, driverMxid, ct);
        await _client.JoinAsUserAsync(room.RoomId, driverMxid, ct);

        // Keyed on the driver, not on "assigned", so a reassignment announces the new driver
        // rather than being de-duplicated away as a repeat of the first.
        var txn = BookingSystemMessageComposer.TransactionId(msg.BookingId, "driver", msg.DriverId.ToString("N"));
        await _client.SendMessageAsync(
            room.RoomId, BookingSystemMessageComposer.DriverAssigned(msg.DriverName), txn, asUserId: null, ct);

        room.RecordDriver(driverMxid);
        await _rooms.SaveChangesAsync(ct);

        _logger.LogInformation("Driver {DriverMxid} seated in room {RoomId} for booking {BookingId}",
            driverMxid, room.RoomId, msg.BookingId);
    }
}
