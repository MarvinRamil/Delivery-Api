using System.Reflection;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Regression coverage for the "cancelled in under a minute" bug: the recurring
/// backstop jobs (process-driver-queue, process-expired-offers) used to mark a
/// booking RejectedByAllDrivers the instant it had zero live offers, even when
/// zero offers had ever been created (no drivers online yet) or when it was well
/// inside the BookingSettings:MaxBroadcastMinutes retry window. Both jobs must now
/// only give up once the booking has genuinely exceeded that window.
/// </summary>
public class BookingBroadcastQueueServiceTests
{
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly IDriverBookingOfferRepository _offerRepo = Substitute.For<IDriverBookingOfferRepository>();
    private readonly IDriverAvailabilityService _availabilityService = Substitute.For<IDriverAvailabilityService>();
    private readonly IDriverOfferNotifier _offerNotifier = Substitute.For<IDriverOfferNotifier>();

    /// <summary>
    /// Giving up on a booking now announces it, so the Payment module can reconcile a booking the
    /// customer already paid for (GitLab #51). Resolved from the job's own scope in production so
    /// the outbox stages onto the same BookingsDbContext that is about to be saved.
    /// </summary>
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();

    private BookingBroadcastQueueService CreateService(
        int maxBroadcastMinutes = 10, int scheduledClaimLeadMinutes = 45, int? maxBookingsPerPulseTick = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["BookingSettings:MaxBroadcastMinutes"] = maxBroadcastMinutes.ToString(),
            ["BookingSettings:ScheduledClaimLeadMinutes"] = scheduledClaimLeadMinutes.ToString(),
        };
        if (maxBookingsPerPulseTick.HasValue)
            settings["BookingSettings:MaxBookingsPerPulseTick"] = maxBookingsPerPulseTick.Value.ToString();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton(_bookingRepo);
        services.AddSingleton(_offerRepo);
        services.AddSingleton(_availabilityService);
        services.AddSingleton(_offerNotifier);
        services.AddSingleton(_publishEndpoint);
        services.AddSingleton<IConfiguration>(configuration);
        var provider = services.BuildServiceProvider();

        return new BookingBroadcastQueueService(provider, NullLogger<BookingBroadcastQueueService>.Instance);
    }

    /// <summary>
    /// A booking that is broadcasting, aged by <paramref name="age"/>. Defaults to an
    /// on-demand booking (scheduled for now) - the give-up deadline is the later of the
    /// retry window and a lead time before the slot, so a booking scheduled hours ahead
    /// is deliberately NOT given up on the retry window alone (#50).
    /// </summary>
    private Booking BroadcastingBooking(TimeSpan age, TimeSpan? scheduledIn = null)
    {
        var booking = new Booking(
            Guid.NewGuid(), "Manila", "Quezon City", "L300", "Boxes",
            DateTime.UtcNow + (scheduledIn ?? TimeSpan.Zero));
        booking.StartBroadcastingToDrivers();
        SetCreatedAt(booking, DateTime.UtcNow - age);

        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }

    /// <summary>The same, in a mode other than Regular — the legacy ctor above is Regular-only.</summary>
    private Booking BroadcastingBooking(DeliveryMode mode, TimeSpan age)
    {
        var booking = new Booking(
            Guid.NewGuid(), "Motorcycle", "Documents", DateTime.UtcNow,
            ServiceType.Immediate, estimatedFare: 150m,
            stops:
            [
                new DeliveryStop(Guid.Empty, 0, "Pickup St", StopType.Pickup, 16.61m, 120.31m),
                new DeliveryStop(Guid.Empty, 1, "Dropoff Ave", StopType.Dropoff, 16.62m, 120.33m),
            ],
            deliveryMode: mode);
        booking.StartBroadcastingToDrivers();
        SetCreatedAt(booking, DateTime.UtcNow - age);

        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }

    /// <summary>Nothing available, so a pulse tick only reschedules — enough to observe ordering.</summary>
    private void NoDriversAvailable()
    {
        _offerRepo.GetByBookingIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DriverBookingOffer>());
        _availabilityService.GetAvailableDriversAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AvailableDriver>());
    }

    /// <summary>The order in which the tick actually asked for drivers, booking by booking.</summary>
    private List<Guid> PulseOrder()
        => _availabilityService.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDriverAvailabilityService.GetAvailableDriversAsync))
            .Select(c => (Guid)c.GetArguments()[0]!)
            .ToList();

    private static void SetCreatedAt(Booking booking, DateTime createdAt)
    {
        // CreatedAt has a protected setter (Entity base) — reflection is the only way
        // to backdate it for a "past the retry window" test scenario.
        typeof(Booking).GetProperty(nameof(Booking.CreatedAt))!.SetValue(booking, createdAt);
    }

    [Fact]
    public async Task ProcessNextDriverInQueue_leaves_a_fresh_zero_offer_booking_broadcasting()
    {
        // No drivers were online when this booking broadcast, so it has zero offers —
        // this must not be treated the same as "all offers exhausted".
        var booking = BroadcastingBooking(age: TimeSpan.FromSeconds(10));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService().ProcessNextDriverInQueueAsync();

        Assert.Equal(BookingAssignmentStatus.BroadcastingToDrivers, booking.AssignmentStatus);
        await _bookingRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessNextDriverInQueue_rejects_a_zero_offer_booking_past_the_window()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        Assert.Equal(BookingAssignmentStatus.RejectedByAllDrivers, booking.AssignmentStatus);
        await _bookingRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessNextDriverInQueue_keeps_broadcasting_a_booking_scheduled_hours_ahead()
    {
        // #50: the give-up window used to be measured from CreatedAt alone, so a booking for
        // later today was abandoned ten minutes after it was made - with hours of chances left.
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15), scheduledIn: TimeSpan.FromHours(4));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        Assert.Equal(BookingAssignmentStatus.BroadcastingToDrivers, booking.AssignmentStatus);
        Assert.Equal(BookingStatus.Pending, booking.Status);
    }

    [Fact]
    public async Task ProcessNextDriverInQueue_gives_up_once_a_scheduled_booking_nears_its_slot()
    {
        // Inside the lead time and still unclaimed: give up now, while the customer still has
        // time to make other arrangements, rather than at the slot itself.
        var booking = BroadcastingBooking(age: TimeSpan.FromHours(3), scheduledIn: TimeSpan.FromMinutes(20));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10, scheduledClaimLeadMinutes: 45).ProcessNextDriverInQueueAsync();

        Assert.Equal(BookingAssignmentStatus.RejectedByAllDrivers, booking.AssignmentStatus);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(Booking.NoDriverFoundReason, booking.CancellationReason);
    }

    [Fact]
    public async Task Giving_up_on_an_on_demand_booking_now_also_moves_its_status()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        // The reported bug: AssignmentStatus moved but Status stayed Pending, so the
        // customer's app showed the booking still searching, forever.
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Null(booking.CancelledBy);
    }

    /// <summary>
    /// GitLab #51. This path is the one that matters most for reconciliation and the easiest to
    /// miss: it never goes through CancelBookingCommandHandler, so publishing only from there
    /// would leave exactly the bookings #50 made visible - paid for, cancelled, nobody told.
    ///
    /// CancelledBy must survive as null. It is what tells Payment this was the platform's failure
    /// rather than the customer's, and therefore the strongest case for returning their money.
    /// </summary>
    [Fact]
    public async Task Giving_up_announces_the_cancellation_as_nobody_s_doing()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        await _publishEndpoint.Received(1).Publish(
            Arg.Is<BookingCancelledEvent>(e =>
                e.BookingId == booking.Id
                && e.CancelledBy == null
                && e.CancellationReason == Booking.NoDriverFoundReason),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The outbox invariant, on the job side. Published after the save the message would be staged
    /// onto a context nothing saves again, and dropped - a cancelled booking whose payment nothing
    /// ever reconciles.
    /// </summary>
    [Fact]
    public async Task Giving_up_announces_before_it_saves_so_the_outbox_commits_both()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        Received.InOrder(() =>
        {
            _publishEndpoint.Publish(Arg.Any<BookingCancelledEvent>(), Arg.Any<CancellationToken>());
            _bookingRepo.SaveChangesAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>A booking still inside its window is not cancelled, so nothing may be announced either.</summary>
    [Fact]
    public async Task A_booking_still_inside_its_window_announces_nothing()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(2));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns((DriverBookingOffer?)null);
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());

        await CreateService(maxBroadcastMinutes: 10).ProcessNextDriverInQueueAsync();

        await _publishEndpoint.DidNotReceive().Publish(
            Arg.Any<BookingCancelledEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessNextDriverInQueue_leaves_a_booking_with_a_live_pending_offer_untouched()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(10));
        var liveOffer = new DriverBookingOffer(booking.Id, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5));
        _bookingRepo.GetBroadcastingBookingsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _offerRepo.GetNextPendingOfferForBookingAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(liveOffer);

        await CreateService().ProcessNextDriverInQueueAsync();

        Assert.Equal(BookingAssignmentStatus.BroadcastingToDrivers, booking.AssignmentStatus);
        await _bookingRepo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessExpiredOffers_does_not_reject_an_early_exhausted_booking_within_the_window()
    {
        // A booking that had a couple of offers, both already rejected/expired, but is
        // still well inside the retry window: the pulse job should get another chance
        // to find newly-available drivers before this booking is given up on.
        var booking = BroadcastingBooking(age: TimeSpan.FromSeconds(30));
        var expiredOffer = new DriverBookingOffer(booking.Id, Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1));
        _offerRepo.GetExpiredOffersAsync(Arg.Any<CancellationToken>()).Returns(new[] { expiredOffer });
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(new[] { expiredOffer });

        await CreateService().ProcessExpiredOffersAsync();

        Assert.Equal(DriverOfferStatus.Expired, expiredOffer.Status);
        Assert.Equal(BookingAssignmentStatus.BroadcastingToDrivers, booking.AssignmentStatus);
    }

    [Fact]
    public async Task ProcessExpiredOffers_rejects_an_exhausted_booking_past_the_window()
    {
        var booking = BroadcastingBooking(age: TimeSpan.FromMinutes(15));
        var expiredOffer = new DriverBookingOffer(booking.Id, Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1));
        _offerRepo.GetExpiredOffersAsync(Arg.Any<CancellationToken>()).Returns(new[] { expiredOffer });
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(new[] { expiredOffer });

        await CreateService(maxBroadcastMinutes: 10).ProcessExpiredOffersAsync();

        Assert.Equal(BookingAssignmentStatus.RejectedByAllDrivers, booking.AssignmentStatus);
    }

    [Theory]
    [InlineData(5, 10)]      // fresh  (age < 1 min)  -> pulse again in 10s
    [InlineData(120, 30)]    // warm   (age < 5 min)  -> 30s
    [InlineData(600, 60)]    // cool   (age < 15 min) -> 60s
    [InlineData(1800, 120)]  // stale  (age >= 15 min) -> 120s
    public async Task PulseBroadcastingBookings_backs_off_pulse_cadence_by_booking_age(
        int ageSeconds, int expectedIntervalSeconds)
    {
        // The due-only pulse now reschedules NextPulseAt with an age-based backoff instead
        // of a flat 1-minute cadence: fresh bookings are revisited fast (so newly-online
        // drivers see them), stale ones back off to keep the due query cheap.
        var booking = BroadcastingBooking(age: TimeSpan.FromSeconds(ageSeconds));
        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] { booking });
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());
        _offerRepo.GetByBookingIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<DriverBookingOffer>());
        _availabilityService.GetAvailableDriversAsync(booking.Id, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AvailableDriver>());

        var before = DateTime.UtcNow;
        await CreateService().PulseBroadcastingBookingsAsync();
        var after = DateTime.UtcNow;

        // Still due-only, still reschedules exactly once.
        await _bookingRepo.Received(1).GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, booking.PulseCount);
        // NextPulseAt == pulseInstant + expectedInterval, where pulseInstant ∈ [before, after].
        Assert.InRange(
            booking.NextPulseAt!.Value,
            before.AddSeconds(expectedIntervalSeconds - 2),
            after.AddSeconds(expectedIntervalSeconds + 2));
        await _bookingRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Each source query is ordered and capped in SQL, so the tick has to re-sort across both. Left
    /// as a plain concat, a low-rank booking that is already broadcasting would be served ahead of a
    /// high-rank one still waiting for its first assignment — and the per-tick cap would then be
    /// decided by which query a booking came from rather than by rank.
    /// </summary>
    [Fact]
    public async Task A_pulse_tick_orders_across_both_sources_not_within_each()
    {
        var broadcastingRegular = BroadcastingBooking(age: TimeSpan.FromMinutes(5));
        var pendingOnDemand = BroadcastingBooking(DeliveryMode.OnDemand, age: TimeSpan.FromSeconds(5));

        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { broadcastingRegular });
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pendingOnDemand });
        NoDriversAvailable();

        await CreateService().PulseBroadcastingBookingsAsync();

        Assert.Equal(new[] { pendingOnDemand.Id, broadcastingRegular.Id }, PulseOrder().ToArray());
    }

    [Fact]
    public async Task The_per_tick_cap_keeps_the_highest_ranked_work_and_defers_the_rest()
    {
        // Without a cap the tick iterated everything, so ordering only rearranged work within a
        // tick and never decided whether a high-rank booking was served at all.
        var regulars = Enumerable.Range(0, 3)
            .Select(i => BroadcastingBooking(age: TimeSpan.FromMinutes(10 + i)))
            .ToArray();
        var onDemand = BroadcastingBooking(DeliveryMode.OnDemand, age: TimeSpan.FromSeconds(1));

        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(regulars.Append(onDemand).ToArray());
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());
        NoDriversAvailable();

        await CreateService(maxBookingsPerPulseTick: 1).PulseBroadcastingBookingsAsync();

        Assert.Equal(onDemand.Id, Assert.Single(PulseOrder()));
    }

    [Fact]
    public async Task The_cap_is_passed_down_to_both_queries()
    {
        // Capping only in memory would still load the whole backlog on every tick.
        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());

        await CreateService(maxBookingsPerPulseTick: 50).PulseBroadcastingBookingsAsync();

        await _bookingRepo.Received(1).GetDueForPulseAsync(
            Arg.Any<DateTime>(), 50, Arg.Any<CancellationToken>());
        await _bookingRepo.Received(1).GetPendingAssignmentDueForDispatchAsync(
            Arg.Any<DateTime>(), 50, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The recency bound on the pending-assignment sweep used to be applied in memory, after loading
    /// every PendingAssignment booking ever created. It is now a query parameter, so it has to
    /// actually be passed.
    /// </summary>
    [Fact]
    public async Task The_pending_assignment_sweep_asks_only_for_recent_bookings()
    {
        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());

        var before = DateTime.UtcNow;
        await CreateService().PulseBroadcastingBookingsAsync();

        await _bookingRepo.Received(1).GetPendingAssignmentDueForDispatchAsync(
            Arg.Is<DateTime>(cutoff =>
                cutoff <= before.AddMinutes(-59) && cutoff >= before.AddMinutes(-61)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A booking's mode has to reach its own pulse cadence. Read from the flat key for every mode,
    /// a slowed-down Pooling booking would keep the fast Regular cadence.
    /// </summary>
    [Fact]
    public async Task A_modes_own_pulse_cadence_is_what_gets_scheduled()
    {
        var pooling = BroadcastingBooking(DeliveryMode.Pooling, age: TimeSpan.FromMinutes(2));  // warm tier

        _bookingRepo.GetDueForPulseAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { pooling });
        _bookingRepo.GetPendingAssignmentDueForDispatchAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Booking>());
        NoDriversAvailable();

        var services = new ServiceCollection();
        services.AddSingleton(_bookingRepo);
        services.AddSingleton(_offerRepo);
        services.AddSingleton(_availabilityService);
        services.AddSingleton(_offerNotifier);
        services.AddSingleton(_publishEndpoint);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BookingSettings:DeliveryModes:Pooling:PulseIntervalWarmSeconds"] = "90",
            }).Build());

        var before = DateTime.UtcNow;
        await new BookingBroadcastQueueService(
            services.BuildServiceProvider(), NullLogger<BookingBroadcastQueueService>.Instance)
            .PulseBroadcastingBookingsAsync();
        var after = DateTime.UtcNow;

        Assert.InRange(pooling.NextPulseAt!.Value, before.AddSeconds(88), after.AddSeconds(92));
    }
}
