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
/// Room provisioning, which runs from a consumer that <em>will</em> be retried. Nearly every test
/// here is about a second delivery not doing damage.
/// </summary>
public class BookingRoomProvisionerTests
{
    private static MatrixOptions Options() => new()
    {
        Enabled = true,
        HomeserverUrl = "http://synapse:8008",
        ServerName = "matrix.bee-app.tech",
        EnvironmentPrefix = "dev",
        AppServiceId = "bee-appservice-dev",
        SenderLocalpart = "bee-dev",
        AsToken = "as",
        HsToken = "hs",
    };

    private sealed class Harness
    {
        public IMatrixAppServiceClient Client = Substitute.For<IMatrixAppServiceClient>();
        public IMatrixUserProvisioner Users = Substitute.For<IMatrixUserProvisioner>();
        public IBookingRoomRepository Rooms = Substitute.For<IBookingRoomRepository>();

        public BookingRoomProvisioner Build()
        {
            Users.EnsureAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(ci => $"@bee_u_dev_{ci.ArgAt<string>(0)}:matrix.bee-app.tech");
            Rooms.AddOrGetAsync(Arg.Any<BookingRoom>(), Arg.Any<CancellationToken>())
                 .Returns(ci => ci.Arg<BookingRoom>());
            return new BookingRoomProvisioner(Client, Users, Rooms, Options().ToOptions(),
                NullLogger<BookingRoomProvisioner>.Instance);
        }
    }

    [Fact]
    public async Task Creates_the_room_with_an_environment_prefixed_alias_and_seats_the_customer()
    {
        var h = new Harness();
        h.Client.CreateOrAdoptRoomAsync(Arg.Any<CreateRoomRequest>(), Arg.Any<CancellationToken>())
                .Returns(new RoomCreationResult("!room:matrix.bee-app.tech", Adopted: false));

        var bookingId = Guid.NewGuid();
        var room = await h.Build().EnsureRoomAsync(bookingId, "BKG-20260823-000123", "cust1", null, "A St", "B Rd");

        var req = (CreateRoomRequest)h.Client.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.CreateOrAdoptRoomAsync))
            .GetArguments()[0]!;

        // The prefix is the isolation boundary; lowercase because Matrix aliases are case-sensitive.
        Assert.Equal("booking-dev-bkg-20260823-000123", req.AliasLocalpart);
        Assert.Equal("BKG-20260823-000123", req.Name);
        Assert.Equal("A St → B Rd", req.Topic);
        Assert.Equal("@bee-dev:matrix.bee-app.tech", req.BotUserId);
        Assert.Single(req.InviteUserIds);

        // Invited AND joined: an appservice user has no client to accept with.
        await h.Client.Received(1).JoinAsUserAsync("!room:matrix.bee-app.tech",
            "@bee_u_dev_cust1:matrix.bee-app.tech", Arg.Any<CancellationToken>());

        Assert.Equal("#booking-dev-bkg-20260823-000123:matrix.bee-app.tech", room.RoomAlias);
        Assert.Equal(bookingId, room.BookingId);
    }

    [Fact]
    public async Task A_redelivered_message_does_not_create_a_second_room()
    {
        // Two rooms for one booking would split the conversation, each party in a different half.
        var h = new Harness();
        var bookingId = Guid.NewGuid();
        var existing = new BookingRoom(bookingId, "BKG-1", "!existing:matrix.bee-app.tech", "#booking-dev-bkg-1:matrix.bee-app.tech");
        h.Rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(existing);

        var room = await h.Build().EnsureRoomAsync(bookingId, "BKG-1", "cust1", null, null, null);

        Assert.Same(existing, room);
        await h.Client.DidNotReceive().CreateOrAdoptRoomAsync(Arg.Any<CreateRoomRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivery_still_re_asserts_the_join()
    {
        // The realistic partial failure is a room that got created but whose join failed. Returning
        // early on redelivery would leave that room empty forever.
        var h = new Harness();
        var bookingId = Guid.NewGuid();
        h.Rooms.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>())
               .Returns(new BookingRoom(bookingId, "BKG-1", "!existing:m", "#a:m"));

        await h.Build().EnsureRoomAsync(bookingId, "BKG-1", "cust1", null, null, null);

        await h.Client.Received(1).JoinAsUserAsync("!existing:m", "@bee_u_dev_cust1:matrix.bee-app.tech", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_race_between_two_deliveries_yields_one_room()
    {
        // Both pass the GetByBookingId check, both create; the unique index decides and the loser
        // must take the winner's row rather than surfacing a duplicate-key error.
        var h = new Harness();
        var bookingId = Guid.NewGuid();
        var winner = new BookingRoom(bookingId, "BKG-1", "!winner:m", "#a:m");
        h.Client.CreateOrAdoptRoomAsync(Arg.Any<CreateRoomRequest>(), Arg.Any<CancellationToken>())
                .Returns(new RoomCreationResult("!loser:m", Adopted: true));

        var provisioner = h.Build();
        // After Build(), which installs the pass-through default this test needs to override.
        h.Rooms.AddOrGetAsync(Arg.Any<BookingRoom>(), Arg.Any<CancellationToken>()).Returns(winner);

        var room = await provisioner.EnsureRoomAsync(bookingId, "BKG-1", "cust1", null, null, null);

        Assert.Same(winner, room);
    }

    [Fact]
    public async Task A_booking_with_no_locations_gets_no_topic_rather_than_a_broken_one()
    {
        var h = new Harness();
        h.Client.CreateOrAdoptRoomAsync(Arg.Any<CreateRoomRequest>(), Arg.Any<CancellationToken>())
                .Returns(new RoomCreationResult("!r:m", false));

        await h.Build().EnsureRoomAsync(Guid.NewGuid(), "BKG-1", "cust1", null, null, null);

        var req = (CreateRoomRequest)h.Client.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == nameof(IMatrixAppServiceClient.CreateOrAdoptRoomAsync))
            .GetArguments()[0]!;
        Assert.Null(req.Topic);
    }
}

/// <summary>The consumer's own decisions, separate from the provisioning it delegates.</summary>
public class BookingChatRoomRequestedConsumerTests
{
    private static BookingChatRoomRequested Message(Guid bookingId, Guid customerId) => new()
    {
        BookingId = bookingId,
        CustomerId = customerId,
        BookingNumber = "BKG-1",
        CreatedAt = DateTime.UtcNow,
    };

    private static (BookingChatRoomRequestedConsumer Consumer, IBookingRoomProvisioner Provisioner, ICustomerIdentityResolver Resolver)
        Build(bool enabled)
    {
        var provisioner = Substitute.For<IBookingRoomProvisioner>();
        var resolver = Substitute.For<ICustomerIdentityResolver>();
        var options = Options.Create(new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://synapse:8008",
            ServerName = "matrix.bee-app.tech",
            EnvironmentPrefix = "dev",
            AppServiceId = "bee-appservice-dev",
            SenderLocalpart = "bee-dev",
            AsToken = "as",
            HsToken = "hs",
        });
        return (new BookingChatRoomRequestedConsumer(provisioner, resolver, options,
            NullLogger<BookingChatRoomRequestedConsumer>.Instance), provisioner, resolver);
    }

    private static ConsumeContext<BookingChatRoomRequested> Context(BookingChatRoomRequested msg)
    {
        var ctx = Substitute.For<ConsumeContext<BookingChatRoomRequested>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task Disabled_acknowledges_the_message_and_does_nothing()
    {
        // Consuming-and-discarding beats leaving the consumer unregistered, which would pile
        // messages up in a queue for as long as the feature stays off.
        var (consumer, provisioner, resolver) = Build(enabled: false);

        await consumer.Consume(Context(Message(Guid.NewGuid(), Guid.NewGuid())));

        await provisioner.DidNotReceiveWithAnyArgs().EnsureRoomAsync(default, default!, default!, default, default, default);
        await resolver.DidNotReceiveWithAnyArgs().ResolveIdentityUserIdAsync(default, default);
    }

    [Fact]
    public async Task Resolves_the_bookings_customer_id_to_an_identity_user_id()
    {
        // Booking.CustomerId is a Bookings-module Customer.Id, NOT the Identity user id Matrix
        // accounts are keyed on. Passing it through unresolved would provision an account nobody
        // can log into.
        var (consumer, provisioner, resolver) = Build(enabled: true);
        var bookingId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        resolver.ResolveIdentityUserIdAsync(customerId, Arg.Any<CancellationToken>()).Returns("identity-user-1");

        await consumer.Consume(Context(Message(bookingId, customerId)));

        await provisioner.Received(1).EnsureRoomAsync(
            bookingId, "BKG-1", "identity-user-1", Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unresolvable_customer_throws_so_the_retry_schedule_applies()
    {
        // Inventing an id would create a room the customer could never reach. Throwing routes this
        // through retry/delayed redelivery, and a permanent failure lands visibly in the error queue.
        var (consumer, provisioner, resolver) = Build(enabled: true);
        resolver.ResolveIdentityUserIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => consumer.Consume(Context(Message(Guid.NewGuid(), Guid.NewGuid()))));

        await provisioner.DidNotReceiveWithAnyArgs().EnsureRoomAsync(default, default!, default!, default, default, default);
    }
}
