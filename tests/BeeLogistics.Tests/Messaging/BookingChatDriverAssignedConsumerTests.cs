using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Consumers;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Seating the driver — the half of provisioning that cannot happen at booking creation, because
/// a booking is created unassigned and may wait minutes for a driver or never get one.
/// </summary>
public class BookingChatDriverAssignedConsumerTests
{
    private readonly IMatrixAppServiceClient _client = Substitute.For<IMatrixAppServiceClient>();
    private readonly IMatrixUserProvisioner _users = Substitute.For<IMatrixUserProvisioner>();
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();

    private BookingChatDriverAssignedConsumer Build(bool enabled = true)
    {
        _users.EnsureAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
              .Returns(ci => $"@bee_u_dev_{ci.ArgAt<string>(0)}:matrix.bee-app.tech");
        _client.SendMessageAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
               .Returns("$evt");

        return new BookingChatDriverAssignedConsumer(_client, _users, _rooms, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        }), NullLogger<BookingChatDriverAssignedConsumer>.Instance);
    }

    private static ConsumeContext<BookingChatDriverAssigned> Context(Guid bookingId, Guid driverId, string? name = "Juan")
    {
        var ctx = Substitute.For<ConsumeContext<BookingChatDriverAssigned>>();
        ctx.Message.Returns(new BookingChatDriverAssigned
        {
            BookingId = bookingId,
            DriverId = driverId,
            DriverName = name,
            AssignedAt = DateTime.UtcNow,
        });
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Invites_then_joins_the_driver_and_announces_it()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var room = new BookingRoom(bookingId, "BKG-1", "!room:matrix.bee-app.tech", "#a:matrix.bee-app.tech");
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(room);

        await Build().Consume(Context(bookingId, driverId));

        var driverMxid = $"@bee_u_dev_{driverId}:matrix.bee-app.tech";
        await _client.Received(1).InviteAsync("!room:matrix.bee-app.tech", driverMxid, Arg.Any<CancellationToken>());
        await _client.Received(1).JoinAsUserAsync("!room:matrix.bee-app.tech", driverMxid, Arg.Any<CancellationToken>());
        await _client.Received(1).SendMessageAsync("!room:matrix.bee-app.tech", Arg.Any<object>(),
            Arg.Any<string>(), null, Arg.Any<CancellationToken>());

        Assert.Equal(driverMxid, room.DriverMatrixUserId);
        await _rooms.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_driver_assigned_before_the_room_exists_retries_rather_than_creating_one()
    {
        // Queues are independent, so acceptance can be consumed before room provisioning. Creating
        // a room here would race the provisioner and could leave the booking with two rooms.
        var bookingId = Guid.NewGuid();
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns((BookingRoom?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build().Consume(Context(bookingId, Guid.NewGuid())));

        await _client.DidNotReceiveWithAnyArgs().InviteAsync(default!, default!, default);
    }

    [Fact]
    public async Task The_notice_is_keyed_on_the_driver_so_a_reassignment_is_not_deduplicated_away()
    {
        // Matrix de-duplicates on the transaction id. Keying on "assigned" alone would make a
        // second driver's announcement vanish as a repeat of the first.
        var bookingId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var room = new BookingRoom(bookingId, "BKG-1", "!room:m", "#a:m");
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(room);

        var consumer = Build();
        await consumer.Consume(Context(bookingId, first));
        await consumer.Consume(Context(bookingId, second));

        var txns = _client.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.SendMessageAsync))
            .Select(c => (string)c.GetArguments()[2]!)
            .ToList();

        Assert.Equal(2, txns.Count);
        Assert.NotEqual(txns[0], txns[1]);
        Assert.Contains(first.ToString("N"), txns[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_driver_delivered_twice_produces_the_same_transaction_id()
    {
        // The retry path: Matrix collapses it to one event rather than posting the notice twice.
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>())
              .Returns(new BookingRoom(bookingId, "BKG-1", "!room:m", "#a:m"));

        var consumer = Build();
        await consumer.Consume(Context(bookingId, driverId));
        await consumer.Consume(Context(bookingId, driverId));

        var txns = _client.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.SendMessageAsync))
            .Select(c => (string)c.GetArguments()[2]!)
            .Distinct()
            .ToList();

        Assert.Single(txns);
    }

    [Fact]
    public async Task Disabled_does_nothing_at_all()
    {
        await Build(enabled: false).Consume(Context(Guid.NewGuid(), Guid.NewGuid()));

        await _rooms.DidNotReceiveWithAnyArgs().GetByBookingIdAsync(default, default);
        await _client.DidNotReceiveWithAnyArgs().InviteAsync(default!, default!, default);
    }

    [Fact]
    public void The_assignment_notice_is_a_notice_not_a_message()
    {
        // m.notice is the Matrix convention for automated posts; clients render it differently, so
        // a status update does not read as the other party talking.
        var content = BookingSystemMessageComposer.DriverAssigned("Juan");
        var json = System.Text.Json.JsonSerializer.Serialize(content);

        Assert.Contains("\"msgtype\":\"m.notice\"", json, StringComparison.Ordinal);
        Assert.Contains("Juan", json, StringComparison.Ordinal);
        Assert.Contains("driver_assigned", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unnamed_driver_still_gets_a_readable_notice()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(BookingSystemMessageComposer.DriverAssigned(null));

        Assert.Contains("A driver has been assigned", json, StringComparison.Ordinal);
    }
}
