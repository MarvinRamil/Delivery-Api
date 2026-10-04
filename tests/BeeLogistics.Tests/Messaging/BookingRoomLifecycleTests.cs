using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Ageing rooms out: active → frozen → purged. The property worth protecting throughout is that
/// the transcript in Postgres survives every step.
/// </summary>
public class BookingRoomLifecycleTests
{
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();
    private readonly IMatrixAppServiceClient _client = Substitute.For<IMatrixAppServiceClient>();

    private BookingRoomLifecycleService Build(bool enabled = true, int freezeHours = 24, int retentionDays = 90) =>
        new(_rooms, _client, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
            RoomFreezeDelayHours = freezeHours,
            RoomRetentionDays = retentionDays,
        }), NullLogger<BookingRoomLifecycleService>.Instance);

    private static BookingRoom Room(string roomId = "!r:m") =>
        new(Guid.NewGuid(), "BKG-1", roomId, "#a:m");

    [Fact]
    public async Task Freezing_asks_for_rooms_whose_booking_ended_before_the_delay()
    {
        var now = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        _rooms.GetDueForFreezeAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<BookingRoom>());

        await Build(freezeHours: 24).FreezeFinishedRoomsAsync(now);

        await _rooms.Received(1).GetDueForFreezeAsync(now.AddHours(-24), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_frozen_room_is_readable_but_nobody_can_post()
    {
        var room = Room();
        _rooms.GetDueForFreezeAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new[] { room });

        var frozen = await Build().FreezeFinishedRoomsAsync(DateTime.UtcNow);

        Assert.Equal(1, frozen);
        Assert.Equal(BookingRoomState.Frozen, room.State);
        Assert.NotNull(room.FrozenAt);

        var content = _client.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.SetPowerLevelsAsync))
            .GetArguments()[1]!;
        var json = System.Text.Json.JsonSerializer.Serialize(content);

        // events_default 100 with users_default 0: retirement, not deletion.
        Assert.Contains("\"events_default\":100", json, StringComparison.Ordinal);
        Assert.Contains("\"users_default\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_unreachable_room_does_not_stop_the_batch()
    {
        var bad = Room("!bad:m");
        var good = Room("!good:m");
        _rooms.GetDueForFreezeAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new[] { bad, good });
        _client.SetPowerLevelsAsync("!bad:m", Arg.Any<object>(), Arg.Any<CancellationToken>())
               .Returns<Task>(_ => throw new HttpRequestException("unreachable"));

        var frozen = await Build().FreezeFinishedRoomsAsync(DateTime.UtcNow);

        Assert.Equal(1, frozen);
        Assert.Equal(BookingRoomState.Active, bad.State);    // stays due, retried next run
        Assert.Equal(BookingRoomState.Frozen, good.State);
    }

    [Fact]
    public async Task Purging_removes_the_room_from_synapse_and_marks_it_without_touching_the_transcript()
    {
        var room = Room();
        room.Freeze();
        _rooms.GetDueForPurgeAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(new[] { room });

        var purged = await Build().PurgeExpiredRoomsAsync(DateTime.UtcNow);

        Assert.Equal(1, purged);
        Assert.Equal(BookingRoomState.Purged, room.State);
        await _client.Received(1).PurgeRoomAsync("!r:m", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retention_of_zero_disables_purging_entirely()
    {
        // A misread config must not delete rooms the moment they freeze.
        await Build(retentionDays: 0).PurgeExpiredRoomsAsync(DateTime.UtcNow);

        await _rooms.DidNotReceiveWithAnyArgs().GetDueForPurgeAsync(default, default, default);
    }

    [Fact]
    public async Task Both_jobs_no_op_while_matrix_is_disabled()
    {
        var svc = Build(enabled: false);

        Assert.Equal(0, await svc.FreezeFinishedRoomsAsync(DateTime.UtcNow));
        Assert.Equal(0, await svc.PurgeExpiredRoomsAsync(DateTime.UtcNow));
        await _rooms.DidNotReceiveWithAnyArgs().GetDueForFreezeAsync(default, default, default);
    }

    [Fact]
    public void The_booking_end_is_stamped_once_and_never_pushed_out()
    {
        // A redelivered cancellation must not keep moving the freeze deadline further away, which
        // would leave a finished booking's room open indefinitely.
        var room = Room();
        var first = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);

        room.MarkBookingEnded(first);
        room.MarkBookingEnded(first.AddHours(5));

        Assert.Equal(first, room.EndedAt);
    }

    [Fact]
    public void Freezing_is_idempotent()
    {
        var room = Room();
        room.Freeze();
        var firstFrozenAt = room.FrozenAt;
        room.Freeze();

        Assert.Equal(firstFrozenAt, room.FrozenAt);
    }
}
