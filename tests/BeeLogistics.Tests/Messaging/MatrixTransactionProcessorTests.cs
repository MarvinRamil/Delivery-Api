using System.Text.Json;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Domain;
using BeeLogistics.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The archive path — what makes an admin able to read a booking conversation without joining the
/// room and without having been present while it happened.
/// </summary>
public class MatrixTransactionProcessorTests
{
    private const string Bot = "@bee-dev:matrix.bee-app.tech";
    private const string CustomerMxid = "@bee_u_dev_cust:matrix.bee-app.tech";
    private const string DriverMxid = "@bee_u_dev_drv:matrix.bee-app.tech";

    private readonly IRoomEventArchive _archive = Substitute.For<IRoomEventArchive>();
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();
    private readonly IMatrixIdentityRepository _identities = Substitute.For<IMatrixIdentityRepository>();
    private readonly IPushDispatcher _push = Substitute.For<IPushDispatcher>();
    private readonly Guid _bookingId = Guid.NewGuid();

    private MatrixTransactionProcessor Build()
    {
        _archive.TryAddAsync(Arg.Any<RoomEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        _identities.GetByMatrixUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(ci => new MatrixIdentity($"bee-{ci.ArgAt<string>(0)}", ci.ArgAt<string>(0)));

        return new MatrixTransactionProcessor(_archive, _rooms, _identities, _push,
            Options.Create(new MatrixOptions
            {
                Enabled = true,
                HomeserverUrl = "http://synapse:8008",
                ServerName = "matrix.bee-app.tech",
                EnvironmentPrefix = "dev",
                AppServiceId = "bee-appservice-dev",
                SenderLocalpart = "bee-dev",
                AsToken = "as",
                HsToken = "hs",
            }), NullLogger<MatrixTransactionProcessor>.Instance);
    }

    private BookingRoom FullyProvisionedRoom()
    {
        var room = new BookingRoom(_bookingId, "BKG-1", "!room:matrix.bee-app.tech", "#a:matrix.bee-app.tech");
        room.RecordCustomer(CustomerMxid);
        room.RecordDriver(DriverMxid);
        _rooms.GetByRoomIdAsync("!room:matrix.bee-app.tech", Arg.Any<CancellationToken>()).Returns(room);
        return room;
    }

    private static JsonElement Transaction(params string[] eventsJson) =>
        JsonDocument.Parse($$"""{"events":[{{string.Join(",", eventsJson)}}]}""").RootElement.Clone();

    private static string Message(string sender, string body, string eventId = "$e1") =>
        """{"event_id":"@ID@","room_id":"!room:matrix.bee-app.tech","sender":"@SENDER@","type":"m.room.message","origin_server_ts":1700000000000,"content":{"msgtype":"m.text","body":"@BODY@"}}"""
            .Replace("@ID@", eventId).Replace("@SENDER@", sender).Replace("@BODY@", body);

    [Fact]
    public async Task Every_event_is_archived_with_its_booking_resolved()
    {
        FullyProvisionedRoom();

        await Build().ProcessAsync(Transaction(Message(CustomerMxid, "On my way")));

        await _archive.Received(1).TryAddAsync(Arg.Is<RoomEvent>(e =>
            e.MatrixEventId == "$e1" &&
            e.BookingId == _bookingId &&
            e.Sender == CustomerMxid &&
            e.EventType == "m.room.message" &&
            e.Body == "On my way" &&
            e.OriginServerTs == 1700000000000), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_event_for_an_unknown_room_is_still_archived()
    {
        // Dropping it would hide exactly the bug worth knowing about — a room we somehow lost track
        // of. Recorded with a null BookingId instead.
        _rooms.GetByRoomIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((BookingRoom?)null);

        await Build().ProcessAsync(Transaction(Message(CustomerMxid, "hello")));

        await _archive.Received(1).TryAddAsync(Arg.Is<RoomEvent>(e => e.BookingId == null), Arg.Any<CancellationToken>());
        await _push.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default, default);
    }

    [Fact]
    public async Task A_customers_message_pushes_to_the_driver_and_vice_versa()
    {
        FullyProvisionedRoom();
        var processor = Build();

        await processor.ProcessAsync(Transaction(Message(CustomerMxid, "On my way", "$a")));
        await _push.Received(1).DispatchAsync(
            Arg.Is<SendPushRequested>(r => r.UserId == $"bee-{DriverMxid}"), "matrix-chat", Arg.Any<CancellationToken>());

        await processor.ProcessAsync(Transaction(Message(DriverMxid, "Almost there", "$b")));
        await _push.Received(1).DispatchAsync(
            Arg.Is<SendPushRequested>(r => r.UserId == $"bee-{CustomerMxid}"), "matrix-chat", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_bots_own_status_notices_do_not_push()
    {
        // Otherwise every status change would buzz both phones twice — once for the status push,
        // once for the bot's own chat line.
        FullyProvisionedRoom();

        await Build().ProcessAsync(Transaction(Message(Bot, "The delivery is on its way.")));

        await _push.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default, default);
    }

    [Fact]
    public async Task A_message_before_the_driver_is_assigned_archives_but_does_not_push()
    {
        var room = new BookingRoom(_bookingId, "BKG-1", "!room:matrix.bee-app.tech", "#a:m");
        room.RecordCustomer(CustomerMxid);   // no driver yet
        _rooms.GetByRoomIdAsync("!room:matrix.bee-app.tech", Arg.Any<CancellationToken>()).Returns(room);

        await Build().ProcessAsync(Transaction(Message(CustomerMxid, "Are you nearby?")));

        await _archive.ReceivedWithAnyArgs(1).TryAddAsync(default!, default);
        await _push.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default, default);
    }

    [Fact]
    public async Task A_redelivered_event_is_not_pushed_again()
    {
        // Synapse retries until it gets a 2xx. Without this, every retry re-notifies.
        FullyProvisionedRoom();
        var processor = Build();
        // After Build(), which installs the "stored" default this test needs to invert.
        _archive.TryAddAsync(Arg.Any<RoomEvent>(), Arg.Any<CancellationToken>()).Returns(false);

        await processor.ProcessAsync(Transaction(Message(CustomerMxid, "On my way")));

        await _push.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default, default);
    }

    [Fact]
    public async Task One_bad_event_does_not_cost_the_rest_of_the_transaction()
    {
        // Synapse retries the whole batch on a non-2xx, so failing here would re-deliver events we
        // already archived and block the queue behind them.
        FullyProvisionedRoom();
        _archive.TryAddAsync(Arg.Is<RoomEvent>(e => e.MatrixEventId == "$bad"), Arg.Any<CancellationToken>())
                .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        await Build().ProcessAsync(Transaction(
            Message(CustomerMxid, "first", "$bad"),
            Message(CustomerMxid, "second", "$good")));

        await _archive.Received(1).TryAddAsync(Arg.Is<RoomEvent>(e => e.MatrixEventId == "$good"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Events_missing_required_fields_are_skipped_not_archived()
    {
        FullyProvisionedRoom();

        await Build().ProcessAsync(Transaction("""{"room_id":"!room:matrix.bee-app.tech","type":"m.room.message"}"""));

        await _archive.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task A_transaction_with_no_events_is_a_no_op()
    {
        await Build().ProcessAsync(JsonDocument.Parse("""{"events":[]}""").RootElement.Clone());
        await Build().ProcessAsync(JsonDocument.Parse("""{}""").RootElement.Clone());

        await _archive.DidNotReceiveWithAnyArgs().TryAddAsync(default!, default);
    }

    [Fact]
    public async Task A_push_failure_does_not_fail_the_transaction()
    {
        // The message is already archived and already in the room; failing would make Synapse
        // redeliver for nothing.
        FullyProvisionedRoom();
        _push.DispatchAsync(Arg.Any<SendPushRequested>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns<Task<Guid>>(_ => throw new HttpRequestException("push down"));

        await Build().ProcessAsync(Transaction(Message(CustomerMxid, "hi")));   // must not throw
    }

    [Fact]
    public async Task Long_messages_are_truncated_before_they_reach_a_lock_screen()
    {
        FullyProvisionedRoom();
        var longBody = new string('a', 300);

        await Build().ProcessAsync(Transaction(Message(CustomerMxid, longBody)));

        await _push.Received(1).DispatchAsync(
            Arg.Is<SendPushRequested>(r => r.Body.Length <= 120 && r.Body.EndsWith("…", StringComparison.Ordinal)),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_membership_event_is_archived_but_never_pushed()
    {
        FullyProvisionedRoom();

        await Build().ProcessAsync(Transaction("""
        {"event_id":"$m1","room_id":"!room:matrix.bee-app.tech","sender":"@bee_u_dev_drv:matrix.bee-app.tech",
         "type":"m.room.member","origin_server_ts":1700000000000,"content":{"membership":"join"}}
        """));

        await _archive.Received(1).TryAddAsync(Arg.Is<RoomEvent>(e => e.EventType == "m.room.member" && e.Body == null),
            Arg.Any<CancellationToken>());
        await _push.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default, default);
    }
}
