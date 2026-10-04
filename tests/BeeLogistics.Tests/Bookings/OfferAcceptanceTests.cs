using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MassTransit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Offer acceptance, including the double-accept race: the booking row carries a
/// PostgreSQL xmin concurrency token, so the losing accept's save throws
/// DbUpdateConcurrencyException and must surface as a clean business failure.
/// </summary>
public class OfferAcceptanceTests
{
    private readonly IDriverBookingOfferRepository _offerRepo = Substitute.For<IDriverBookingOfferRepository>();
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly ICustomerIdentityResolver _identityResolver = Substitute.For<ICustomerIdentityResolver>();
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly Dictionary<string, string?> _settings = new();

    public OfferAcceptanceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        // Default: any driver looked up is active and approved (onboarded).
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns(callInfo => new ApplicationUser
        {
            Id = callInfo.Arg<string>(),
            Role = "Driver",
            IsActive = true,
            IsOnboarded = true,
        });
        // Every accept opens a capacity scope; in production it is a real transaction.
        _bookingRepo.BeginDriverCapacityScopeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDriverCapacityScope>());
    }

    /// <summary>Captures the chat-room event the accept publishes (#78).</summary>
    private readonly IPublishEndpoint _publishEndpoint = Substitute.For<IPublishEndpoint>();

    private AcceptBookingOfferCommandHandler Handler()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();
        return new(_offerRepo, _bookingRepo, _notifications, _identityResolver, _userManager,
            configuration, _publishEndpoint, NullLogger<AcceptBookingOfferCommandHandler>.Instance);
    }

    /// <summary>Pretends the driver is already holding this much work.</summary>
    private void DriverIsHolding(int active, int scheduledAhead) =>
        _bookingRepo.GetDriverWorkloadAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new DriverWorkload(active, scheduledAhead));

    private (DriverBookingOffer Offer, Booking Booking) PendingOfferWithBooking(
        Guid driverId, DateTime? scheduleDate = null)
    {
        var booking = new Booking(
            Guid.NewGuid(), "Manila", "Quezon City", "L300", "Boxes",
            scheduleDate ?? DateTime.UtcNow.AddHours(1));
        var offer = new DriverBookingOffer(booking.Id, driverId, DateTime.UtcNow.AddMinutes(10));

        _offerRepo.GetByIdAsync(offer.Id, Arg.Any<CancellationToken>()).Returns(offer);
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return (offer, booking);
    }

    [Fact]
    public async Task Accepting_a_pending_offer_assigns_the_driver()
    {
        var driverId = Guid.NewGuid();
        var (offer, booking) = PendingOfferWithBooking(driverId);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(DriverOfferStatus.Accepted, offer.Status);
        Assert.Equal(driverId, booking.SelectedDriverId);
        await _offerRepo.Received(1).CancelAllPendingOffersForBookingAsync(
            booking.Id, offer.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Losing_the_concurrency_race_returns_already_accepted_failure()
    {
        var driverId = Guid.NewGuid();
        var (offer, _) = PendingOfferWithBooking(driverId);
        // Another driver's accept committed first: the xmin token check fails on save.
        _bookingRepo.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("xmin conflict"));

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Another driver", result.Error);
        // The loser must not notify the customer as if it won.
        await _notifications.DidNotReceiveWithAnyArgs().SendToUserAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Not_onboarded_driver_cannot_accept_an_offer()
    {
        var driverId = Guid.NewGuid();
        var (offer, booking) = PendingOfferWithBooking(driverId);
        _userManager.FindByIdAsync(driverId.ToString()).Returns(new ApplicationUser
        {
            Id = driverId.ToString(),
            Role = "Driver",
            IsActive = true,
            IsOnboarded = false, // registered but not yet approved
        });

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not approved", result.Error);
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
        Assert.Null(booking.SelectedDriverId);
    }

    [Fact]
    public async Task Inactive_driver_cannot_accept_an_offer()
    {
        var driverId = Guid.NewGuid();
        var (offer, _) = PendingOfferWithBooking(driverId);
        _userManager.FindByIdAsync(driverId.ToString()).Returns(new ApplicationUser
        {
            Id = driverId.ToString(),
            Role = "Driver",
            IsActive = false,
            IsOnboarded = true,
        });

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
    }

    [Fact]
    public async Task Unknown_driver_cannot_accept_an_offer()
    {
        var driverId = Guid.NewGuid();
        var (offer, _) = PendingOfferWithBooking(driverId);
        _userManager.FindByIdAsync(driverId.ToString()).Returns((ApplicationUser?)null);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
    }

    [Fact]
    public async Task Accepting_someone_elses_offer_fails()
    {
        var (offer, _) = PendingOfferWithBooking(Guid.NewGuid());

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
    }

    // --- Concurrency caps (#55) -------------------------------------------------------------
    // A driver could previously accept an unlimited number of bookings. Every one of those
    // customers saw a driver assigned; most of them were never going to be served.

    [Fact]
    public async Task A_driver_at_the_active_limit_cannot_accept_more_work_for_now()
    {
        var driverId = Guid.NewGuid();
        // On-demand: the slot is now, so it counts against the active limit.
        var (offer, booking) = PendingOfferWithBooking(driverId, DateTime.UtcNow);
        DriverIsHolding(active: DriverCapacityPolicy.DefaultMaxActive, scheduledAhead: 0);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("active bookings", result.Error);
        // Nothing moved: the offer stays claimable by someone who can actually serve it.
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
        Assert.Null(booking.SelectedDriverId);
        await _offerRepo.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task A_driver_one_below_the_active_limit_can_still_accept()
    {
        var driverId = Guid.NewGuid();
        var (offer, booking) = PendingOfferWithBooking(driverId, DateTime.UtcNow);
        DriverIsHolding(active: DriverCapacityPolicy.DefaultMaxActive - 1, scheduledAhead: 0);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(driverId, booking.SelectedDriverId);
    }

    [Fact]
    public async Task A_driver_at_the_scheduled_limit_cannot_claim_another_future_slot()
    {
        var driverId = Guid.NewGuid();
        var (offer, _) = PendingOfferWithBooking(driverId, DateTime.UtcNow.AddDays(3));
        DriverIsHolding(active: 0, scheduledAhead: DriverCapacityPolicy.DefaultMaxScheduledAhead);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("upcoming scheduled bookings", result.Error);
    }

    [Fact]
    public async Task The_two_limits_are_counted_separately()
    {
        var driverId = Guid.NewGuid();
        // Booked solid for the rest of the week, but free right now: today's work must still
        // be acceptable, or a driver planning ahead would be shut out of on-demand entirely.
        DriverIsHolding(active: 0, scheduledAhead: DriverCapacityPolicy.DefaultMaxScheduledAhead);
        var (onDemandOffer, onDemandBooking) = PendingOfferWithBooking(driverId, DateTime.UtcNow);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(onDemandOffer.Id, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(driverId, onDemandBooking.SelectedDriverId);

        // ...and the mirror case: mid-delivery, but next Tuesday is still claimable.
        DriverIsHolding(active: DriverCapacityPolicy.DefaultMaxActive, scheduledAhead: 0);
        var (scheduledOffer, scheduledBooking) = PendingOfferWithBooking(driverId, DateTime.UtcNow.AddDays(4));

        var scheduledResult = await Handler().Handle(
            new AcceptBookingOfferCommand(scheduledOffer.Id, driverId), CancellationToken.None);

        Assert.True(scheduledResult.IsSuccess);
        Assert.Equal(driverId, scheduledBooking.SelectedDriverId);
    }

    [Fact]
    public async Task A_slot_inside_the_lead_window_counts_as_active_work_not_a_future_claim()
    {
        var driverId = Guid.NewGuid();
        // 20 minutes out is inside the 45-minute lead window: the driver has to be heading
        // there, so it must be governed by the active limit rather than the looser one.
        var (offer, _) = PendingOfferWithBooking(driverId, DateTime.UtcNow.AddMinutes(20));
        DriverIsHolding(active: DriverCapacityPolicy.DefaultMaxActive, scheduledAhead: 0);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("active bookings", result.Error);
    }

    [Fact]
    public async Task Both_limits_are_configurable()
    {
        var driverId = Guid.NewGuid();
        _settings["BookingSettings:MaxActiveBookingsPerDriver"] = "1";
        var (offer, _) = PendingOfferWithBooking(driverId, DateTime.UtcNow);
        DriverIsHolding(active: 1, scheduledAhead: 0);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("limit 1", result.Error);
    }

    [Fact]
    public async Task The_capacity_check_runs_inside_a_scope_that_is_only_committed_on_success()
    {
        var driverId = Guid.NewGuid();
        var scope = Substitute.For<IDriverCapacityScope>();
        _bookingRepo.BeginDriverCapacityScopeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(scope);

        // Rejected accept: the scope must be opened (it is what makes the count trustworthy)
        // but never committed.
        var (rejectedOffer, _) = PendingOfferWithBooking(driverId, DateTime.UtcNow);
        DriverIsHolding(active: DriverCapacityPolicy.DefaultMaxActive, scheduledAhead: 0);
        await Handler().Handle(new AcceptBookingOfferCommand(rejectedOffer.Id, driverId), CancellationToken.None);

        await _bookingRepo.Received(1).BeginDriverCapacityScopeAsync(driverId, Arg.Any<CancellationToken>());
        await scope.DidNotReceiveWithAnyArgs().CommitAsync(default);

        // Successful accept: committed, so the lock is not released before the save lands.
        var (acceptedOffer, _) = PendingOfferWithBooking(driverId, DateTime.UtcNow);
        DriverIsHolding(active: 0, scheduledAhead: 0);
        await Handler().Handle(new AcceptBookingOfferCommand(acceptedOffer.Id, driverId), CancellationToken.None);

        await scope.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Accepting_an_expired_offer_fails()
    {
        var driverId = Guid.NewGuid();
        var booking = new Booking(
            Guid.NewGuid(), "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1));
        var offer = new DriverBookingOffer(booking.Id, driverId, DateTime.UtcNow.AddMinutes(-1));
        _offerRepo.GetByIdAsync(offer.Id, Arg.Any<CancellationToken>()).Returns(offer);
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);

        var result = await Handler().Handle(
            new AcceptBookingOfferCommand(offer.Id, driverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DriverOfferStatus.Pending, offer.Status);
    }
}
