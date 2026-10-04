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
/// Booking-status notices: the part that makes the room carry the trip's history rather than only
/// chatter.
/// </summary>
public class BookingChatStatusChangedConsumerTests
{
    private readonly IMatrixAppServiceClient _client = Substitute.For<IMatrixAppServiceClient>();
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();

    private BookingChatStatusChangedConsumer Build(bool enabled = true) =>
        new(_client, _rooms, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        }), NullLogger<BookingChatStatusChangedConsumer>.Instance);

    private static ConsumeContext<BookingChatStatusChanged> Context(Guid bookingId, string status)
    {
        var ctx = Substitute.For<ConsumeContext<BookingChatStatusChanged>>();
        ctx.Message.Returns(new BookingChatStatusChanged
        {
            BookingId = bookingId,
            Status = status,
            ChangedAt = DateTime.UtcNow,
        });
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private void RoomExistsFor(Guid bookingId) =>
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>())
              .Returns(new BookingRoom(bookingId, "BKG-1", "!room:m", "#a:m"));

    [Theory]
    [InlineData("DriverAssigned")]
    [InlineData("PickedUp")]
    [InlineData("InTransit")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Narrated_statuses_post_a_notice(string status)
    {
        var bookingId = Guid.NewGuid();
        RoomExistsFor(bookingId);

        await Build().Consume(Context(bookingId, status));

        await _client.Received(1).SendMessageAsync("!room:m", Arg.Any<object>(), Arg.Any<string>(), null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Pending")]     // the state the room is created in
    [InlineData("Confirmed")]   // already announced by BookingChatDriverAssigned
    [InlineData("Something")]
    public async Task Statuses_we_deliberately_do_not_narrate_post_nothing(string status)
    {
        // Posting these would say the same thing twice and train people to ignore the bot.
        var bookingId = Guid.NewGuid();
        RoomExistsFor(bookingId);

        await Build().Consume(Context(bookingId, status));

        await _client.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task A_missing_room_drops_the_notice_instead_of_blocking_the_queue()
    {
        // Unlike driver assignment, where retrying until the room exists is the whole point, a
        // missing room here means the booking never got one — retrying cannot conjure it, and a
        // status line is not worth stalling a queue over.
        var bookingId = Guid.NewGuid();
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns((BookingRoom?)null);

        await Build().Consume(Context(bookingId, "InTransit"));   // must not throw

        await _client.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task A_redelivery_reuses_the_transaction_id_so_the_line_is_not_repeated()
    {
        var bookingId = Guid.NewGuid();
        RoomExistsFor(bookingId);

        var consumer = Build();
        await consumer.Consume(Context(bookingId, "InTransit"));
        await consumer.Consume(Context(bookingId, "InTransit"));

        var txns = _client.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.SendMessageAsync))
            .Select(c => (string)c.GetArguments()[2]!)
            .Distinct().ToList();

        Assert.Single(txns);
    }

    [Fact]
    public async Task Different_statuses_get_different_transaction_ids()
    {
        // Otherwise the second status would be de-duplicated away as a repeat of the first.
        var bookingId = Guid.NewGuid();
        RoomExistsFor(bookingId);

        var consumer = Build();
        await consumer.Consume(Context(bookingId, "PickedUp"));
        await consumer.Consume(Context(bookingId, "InTransit"));

        var txns = _client.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.SendMessageAsync))
            .Select(c => (string)c.GetArguments()[2]!)
            .Distinct().ToList();

        Assert.Equal(2, txns.Count);
    }

    [Fact]
    public async Task Disabled_does_nothing()
    {
        await Build(enabled: false).Consume(Context(Guid.NewGuid(), "InTransit"));

        await _rooms.DidNotReceiveWithAnyArgs().GetByBookingIdAsync(default, default);
    }

    [Fact]
    public void Every_narrated_status_is_a_notice_carrying_a_machine_readable_status()
    {
        // The structured field is what lets a client render a timeline chip instead of parsing prose.
        foreach (var status in new[] { "DriverAssigned", "PickedUp", "InTransit", "Completed", "Cancelled" })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(BookingSystemMessageComposer.BookingStatus(status));
            Assert.Contains("\"msgtype\":\"m.notice\"", json, StringComparison.Ordinal);
            Assert.Contains($"\"app_bee_status\":\"{status}\"", json, StringComparison.Ordinal);
        }
    }
}
