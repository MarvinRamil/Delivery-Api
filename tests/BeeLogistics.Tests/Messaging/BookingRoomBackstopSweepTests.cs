using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The backstop for bookings whose chat room was never created.
/// </summary>
/// <remarks>
/// This exists because the retry budget is finite — 1s/5s/30s, then 1/5/15 min. A homeserver
/// outage longer than roughly 20 minutes leaves the provisioning message in the error queue and
/// that booking permanently without a chat room, with nothing to notice. The homeserver is on-prem
/// and production is in the cloud, so an outage that long is a question of when, not if.
/// </remarks>
public class BookingRoomBackstopSweepTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IBookingRoomRepository _rooms = Substitute.For<IBookingRoomRepository>();
    private readonly IBookingRoomProvisioner _provisioner = Substitute.For<IBookingRoomProvisioner>();
    private readonly ICustomerIdentityResolver _identity = Substitute.For<ICustomerIdentityResolver>();

    private BookingRoomBackstopSweep Build(bool enabled = true) =>
        new(_mediator, _rooms, _provisioner, _identity, Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        }), NullLogger<BookingRoomBackstopSweep>.Instance);

    private static BookingForChatRoom Booking(Guid id) =>
        new(id, Guid.NewGuid(), "BKG-1", "A", "B", DateTime.UtcNow);

    private void RecentBookingsAre(params BookingForChatRoom[] bookings) =>
        _mediator.Send(Arg.Any<GetBookingsCreatedSinceQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Result.Ok<IReadOnlyList<BookingForChatRoom>>(bookings));

    [Fact]
    public async Task Bookings_that_already_have_rooms_are_left_alone()
    {
        var withRoom = Booking(Guid.NewGuid());
        RecentBookingsAre(withRoom);
        _rooms.GetExistingBookingIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
              .Returns(new[] { withRoom.BookingId });

        Assert.Equal(0, await Build().SweepAsync(DateTime.UtcNow));
        await _provisioner.DidNotReceiveWithAnyArgs().EnsureRoomAsync(default, default!, default!, default, default, default);
    }

    [Fact]
    public async Task A_booking_with_no_room_is_provisioned()
    {
        var orphan = Booking(Guid.NewGuid());
        RecentBookingsAre(orphan);
        _rooms.GetExistingBookingIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<Guid>());
        _identity.ResolveIdentityUserIdAsync(orphan.CustomerId, Arg.Any<CancellationToken>()).Returns("identity-1");

        Assert.Equal(1, await Build().SweepAsync(DateTime.UtcNow));

        await _provisioner.Received(1).EnsureRoomAsync(
            orphan.BookingId, "BKG-1", "identity-1", Arg.Any<string?>(), "A", "B", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_failure_does_not_abandon_the_rest_of_the_batch()
    {
        var bad = Booking(Guid.NewGuid());
        var good = Booking(Guid.NewGuid());
        RecentBookingsAre(bad, good);
        _rooms.GetExistingBookingIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<Guid>());
        _identity.ResolveIdentityUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("identity-1");
        _provisioner.EnsureRoomAsync(bad.BookingId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                                     Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<BeeLogistics.Modules.Messaging.Domain.BookingRoom>>(_ => throw new HttpRequestException("down"));

        Assert.Equal(1, await Build().SweepAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task A_booking_whose_customer_has_no_identity_is_skipped_not_retried_forever()
    {
        // Provisioning would create a room the customer could never reach.
        var orphan = Booking(Guid.NewGuid());
        RecentBookingsAre(orphan);
        _rooms.GetExistingBookingIdsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<Guid>());
        _identity.ResolveIdentityUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        Assert.Equal(0, await Build().SweepAsync(DateTime.UtcNow));
        await _provisioner.DidNotReceiveWithAnyArgs().EnsureRoomAsync(default, default!, default!, default, default, default);
    }

    [Fact]
    public async Task The_lookback_is_longer_than_the_full_retry_schedule()
    {
        // Otherwise the sweep would race messages still legitimately in flight and double-provision.
        RecentBookingsAre();
        var now = DateTime.UtcNow;

        await Build().SweepAsync(now);

        var query = (GetBookingsCreatedSinceQuery)_mediator.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(IMediator.Send))
            .GetArguments()[0]!;

        var lookback = now - query.SinceUtc;
        Assert.True(lookback >= TimeSpan.FromHours(1), $"lookback {lookback} is shorter than the retry schedule");
        Assert.True(query.Limit > 0);
    }

    [Fact]
    public async Task Disabled_does_not_even_query()
    {
        Assert.Equal(0, await Build(enabled: false).SweepAsync(DateTime.UtcNow));
        await _mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<GetBookingsCreatedSinceQuery>(), default);
    }
}
