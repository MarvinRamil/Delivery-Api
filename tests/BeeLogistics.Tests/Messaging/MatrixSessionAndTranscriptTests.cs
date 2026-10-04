using System.Security.Claims;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.DTOs;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Modules.Messaging.Domain;
using BeeLogistics.Modules.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// Session issuance: the only door through which a client gets Matrix credentials.
/// </summary>
public class MatrixSessionControllerTests
{
    private readonly IMatrixSessionIssuer _issuer = Substitute.For<IMatrixSessionIssuer>();

    private MatrixSessionController Build(ClaimsPrincipal user, bool enabled = true)
    {
        _issuer.IssueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(ci => new MatrixSessionDto
               {
                   HomeserverUrl = "https://matrix.bee-app.tech",
                   UserId = $"@bee_u_dev_{ci.ArgAt<string>(0)}:matrix.bee-app.tech",
                   AccessToken = "syt_token",
                   DeviceId = "DEV1",
               });

        var controller = new MatrixSessionController(_issuer, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            PublicHomeserverUrl = "https://matrix.bee-app.tech",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        }), NullLogger<MatrixSessionController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private static ClaimsPrincipal UserWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public async Task The_session_is_issued_for_the_caller_on_the_token_not_the_request_body()
    {
        // The security property that matters here: accepting a user id from the body would let any
        // authenticated user mint credentials for anyone else and read their conversations.
        var controller = Build(UserWith(new Claim(ClaimTypes.NameIdentifier, "user-1")));

        var result = await controller.CreateSession(new CreateMatrixSessionRequest { Platform = "web" }, default);

        Assert.IsType<OkObjectResult>(result);
        await _issuer.Received(1).IssueAsync("user-1", Arg.Any<string?>(), "web", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_token_with_no_user_id_is_rejected()
    {
        var result = await Build(UserWith(new Claim("unrelated", "x"))).CreateSession(null, default);

        Assert.IsType<UnauthorizedObjectResult>(result);
        await _issuer.DidNotReceiveWithAnyArgs().IssueAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task A_sub_claim_works_when_NameIdentifier_is_absent()
    {
        // Clerk-issued tokens carry sub; the legacy scheme maps NameIdentifier.
        var result = await Build(UserWith(new Claim("sub", "clerk-user"))).CreateSession(null, default);

        Assert.IsType<OkObjectResult>(result);
        await _issuer.Received(1).IssueAsync("clerk-user", Arg.Any<string?>(), "web", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("../../etc")]
    [InlineData("a-very-long-platform-string-from-a-hostile-client")]
    public async Task An_unknown_platform_is_rejected(string platform)
    {
        // Platform is half of a unique index and lands in a device display name. Unbounded, a
        // caller could create unlimited devices for themselves.
        var controller = Build(UserWith(new Claim(ClaimTypes.NameIdentifier, "user-1")));

        var result = await controller.CreateSession(new CreateMatrixSessionRequest { Platform = platform }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        await _issuer.DidNotReceiveWithAnyArgs().IssueAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task Platform_defaults_to_web_and_is_case_insensitive()
    {
        var controller = Build(UserWith(new Claim(ClaimTypes.NameIdentifier, "user-1")));

        await controller.CreateSession(null, default);
        await controller.CreateSession(new CreateMatrixSessionRequest { Platform = "Driver-Android" }, default);

        await _issuer.Received(1).IssueAsync("user-1", Arg.Any<string?>(), "web", Arg.Any<CancellationToken>());
        await _issuer.Received(1).IssueAsync("user-1", Arg.Any<string?>(), "driver-android", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_chat_disabled_the_endpoint_says_so_rather_than_minting_a_useless_token()
    {
        var result = await Build(UserWith(new Claim(ClaimTypes.NameIdentifier, "user-1")), enabled: false)
            .CreateSession(null, default);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }
}

/// <summary>
/// Admin transcript access — the feature's whole reason for keeping rooms unencrypted.
/// </summary>
public class BookingTranscriptTests
{
    private readonly IRoomEventArchive _archive = Substitute.For<IRoomEventArchive>();
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();

    private const string Bot = "@bee-dev:matrix.bee-app.tech";
    private const string Cust = "@bee_u_dev_c:matrix.bee-app.tech";
    private const string Drv = "@bee_u_dev_d:matrix.bee-app.tech";

    private BookingTranscriptReader Reader() =>
        new(_archive, _rooms, Options.Create(new MatrixOptions
        {
            Enabled = true,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        }));

    private BookingRoom Room(Guid bookingId, BookingRoomState? state = null)
    {
        var room = new BookingRoom(bookingId, "BKG-1", "!room:m", "#a:m");
        room.RecordCustomer(Cust);
        room.RecordDriver(Drv);
        if (state == BookingRoomState.Purged) room.MarkPurged();
        _rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(room);
        return room;
    }

    private static RoomEvent Event(Guid bookingId, string sender, string body, long ts) =>
        new($"$e{ts}", "!room:m", bookingId, sender, "m.room.message", body, "{}", ts);

    [Fact]
    public async Task Each_sender_is_labelled_with_their_part_in_the_booking()
    {
        // Otherwise a reviewer has to decode @bee_u_dev_<guid> to work out who said what.
        var bookingId = Guid.NewGuid();
        Room(bookingId);
        _archive.GetForBookingAsync(bookingId, 0, 100, Arg.Any<CancellationToken>()).Returns(new[]
        {
            Event(bookingId, Bot, "Driver assigned.", 1),
            Event(bookingId, Cust, "Where are you?", 2),
            Event(bookingId, Drv, "Two minutes.", 3),
            Event(bookingId, "@someone_else:matrix.bee-app.tech", "?", 4),
        });
        _archive.CountForBookingAsync(bookingId, Arg.Any<CancellationToken>()).Returns(4);

        var transcript = await Reader().ReadAsync(bookingId, 0, 100);

        Assert.NotNull(transcript);
        Assert.Equal(new[] { "System", "Customer", "Driver", "Unknown" },
            transcript!.Messages.Select(m => m.SenderRole).ToArray());
        Assert.Equal(4, transcript.TotalMessages);
    }

    [Fact]
    public async Task A_booking_with_no_room_reads_as_null()
    {
        _rooms.GetByBookingIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((BookingRoom?)null);

        Assert.Null(await Reader().ReadAsync(Guid.NewGuid(), 0, 100));
    }

    [Fact]
    public async Task A_purged_room_still_has_a_readable_transcript()
    {
        // The point of archiving into our own database: purging Synapse reclaims storage, it does
        // not destroy the evidence a dispute needs months later.
        var bookingId = Guid.NewGuid();
        Room(bookingId, BookingRoomState.Purged);
        _archive.GetForBookingAsync(bookingId, 0, 100, Arg.Any<CancellationToken>())
                .Returns(new[] { Event(bookingId, Cust, "still here", 1) });
        _archive.CountForBookingAsync(bookingId, Arg.Any<CancellationToken>()).Returns(1);

        var transcript = await Reader().ReadAsync(bookingId, 0, 100);

        Assert.Equal("Purged", transcript!.RoomState);
        Assert.Single(transcript.Messages);
    }

    [Fact]
    public async Task Timestamps_come_from_synapse_rather_than_being_re_derived()
    {
        var bookingId = Guid.NewGuid();
        Room(bookingId);
        _archive.GetForBookingAsync(bookingId, 0, 100, Arg.Any<CancellationToken>())
                .Returns(new[] { Event(bookingId, Cust, "x", 1700000000000) });

        var transcript = await Reader().ReadAsync(bookingId, 0, 100);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            transcript!.Messages[0].SentAt);
    }

    [Fact]
    public async Task An_oversized_page_request_is_capped()
    {
        // An uncapped take on a chat archive pulls an entire table over the wire.
        var reader = Substitute.For<IBookingTranscriptReader>();
        var controller = new BookingTranscriptController(reader, NullLogger<BookingTranscriptController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        reader.ReadAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns((BookingTranscriptDto?)null);

        await controller.GetTranscript(Guid.NewGuid(), skip: -5, take: 100_000);

        await reader.Received(1).ReadAsync(Arg.Any<Guid>(), 0, 200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_booking_with_no_room_returns_404_rather_than_an_empty_transcript()
    {
        // An empty list would read as "they never spoke", which is a different and misleading claim.
        var reader = Substitute.For<IBookingTranscriptReader>();
        reader.ReadAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns((BookingTranscriptDto?)null);

        var controller = new BookingTranscriptController(reader, NullLogger<BookingTranscriptController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        Assert.IsType<NotFoundObjectResult>(await controller.GetTranscript(Guid.NewGuid()));
    }
}
