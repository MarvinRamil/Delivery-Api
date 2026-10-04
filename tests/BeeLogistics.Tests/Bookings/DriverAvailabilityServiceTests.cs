using System.Globalization;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MockQueryable.NSubstitute;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Driver matching: H3 geo-index candidates verified against Identity, with the
/// full-scan fallback when the index is empty.
/// </summary>
public class DriverAvailabilityServiceTests
{
    private readonly IBookingRepository _bookingRepo = Substitute.For<IBookingRepository>();
    private readonly IDriverBookingOfferRepository _offerRepo = Substitute.For<IDriverBookingOfferRepository>();
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDriverGeoIndex _geoIndex = Substitute.For<IDriverGeoIndex>();
    private readonly IDistanceCalculationService _distanceService = Substitute.For<IDistanceCalculationService>();

    public DriverAvailabilityServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    /// <summary>
    /// Drivers the wallet gate will allow through, or null for "every candidate can pay".
    /// </summary>
    /// <remarks>
    /// Defaults to letting everyone through so the ranking tests below stay about ranking. The
    /// gate itself is exercised by the wallet tests, which set this.
    /// </remarks>
    private IReadOnlyList<Guid>? _driversWhoCanPayCommission;

    private DriverAvailabilityService CreateService(
        decimal proximityRadiusKm = 0, params (string Key, string? Value)[] extraSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["BookingSettings:MaxOffersPerBroadcast"] = "15",
            ["BookingSettings:ProximityRadiusKm"] = proximityRadiusKm.ToString(),
        };
        foreach (var (key, value) in extraSettings)
            settings[key] = value;

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDriversEligibleForCashJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => _driversWhoCanPayCommission
                             ?? ((GetDriversEligibleForCashJobQuery)call[0]).DriverIds.ToList());

        return new(
            _bookingRepo,
            _offerRepo,
            _userManager,
            mediator,
            _distanceService,
            _geoIndex,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<DriverAvailabilityService>.Instance);
    }

    private Booking BookingWithPickup(decimal lat = 14.5995m, decimal lng = 120.9842m)
    {
        var booking = new Booking(
            Guid.NewGuid(), "Manila", "Quezon City", "L300", "Boxes", DateTime.UtcNow.AddHours(1),
            pickupLatitude: lat, pickupLongitude: lng,
            dropoffLatitude: lat + 0.1m, dropoffLongitude: lng + 0.1m);
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }

    /// <summary>The same booking, in a mode other than Regular. The legacy ctor is Regular-only.</summary>
    private Booking BookingWithPickup(DeliveryMode mode, decimal lat = 14.5995m, decimal lng = 120.9842m)
    {
        var booking = new Booking(
            Guid.NewGuid(), "Motorcycle", "Documents", DateTime.UtcNow.AddHours(1),
            ServiceType.Immediate, estimatedFare: 150m,
            stops:
            [
                new DeliveryStop(Guid.Empty, 0, "Pickup St", StopType.Pickup, lat, lng),
                new DeliveryStop(Guid.Empty, 1, "Dropoff Ave", StopType.Dropoff, lat + 0.1m, lng + 0.1m),
            ],
            deliveryMode: mode);
        _bookingRepo.GetByIdAsync(booking.Id, Arg.Any<CancellationToken>()).Returns(booking);
        return booking;
    }

    private static ApplicationUser Driver(Guid id, bool active = true, string role = "Driver", bool onboarded = true, bool online = true) => new()
    {
        Id = id.ToString(),
        Role = role,
        IsActive = active,
        IsOnboarded = onboarded,
        IsOnline = online,
    };

    private void UsersAre(params ApplicationUser[] users)
    {
        // Build the mock before calling Returns: creating a substitute inside the
        // Returns(...) argument resets NSubstitute's last-call tracking.
        var mockUsers = users.AsQueryable().BuildMockDbSet();
        _userManager.Users.Returns(mockUsers);
    }

    private static void SetCreatedAt(Booking booking, DateTime createdAt)
    {
        // CreatedAt has a protected setter (Entity base) — reflection backdates it
        // to simulate an aging booking for the progressive-radius tests.
        typeof(Booking).GetProperty(nameof(Booking.CreatedAt))!.SetValue(booking, createdAt);
    }

    [Fact]
    public async Task H3_candidates_are_verified_against_identity_and_sorted_by_distance()
    {
        var booking = BookingWithPickup();
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();
        var inactive = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(far, 14.62m, 121.0m, 5.0),
                new(near, 14.60m, 120.99m, 1.0),
                new(inactive, 14.61m, 120.98m, 2.0),
            });
        UsersAre(Driver(near), Driver(far), Driver(inactive, active: false));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(2, result.Count);
        Assert.Equal(near, result[0].DriverId);
        Assert.Equal(1, result[0].SequenceNumber);
        Assert.Equal(far, result[1].DriverId);
        Assert.Equal(2, result[1].SequenceNumber);
        Assert.DoesNotContain(result, d => d.DriverId == inactive);
    }

    [Fact]
    public async Task Non_driver_roles_are_excluded_from_h3_candidates()
    {
        var booking = BookingWithPickup();
        var driver = Guid.NewGuid();
        var customer = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(driver, 14.60m, 120.99m, 1.0),
                new(customer, 14.61m, 120.98m, 0.5),
            });
        UsersAre(Driver(driver), Driver(customer, role: "Customer"));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(driver, result[0].DriverId);
    }

    [Fact]
    public async Task Offline_drivers_are_excluded_from_h3_candidates()
    {
        var booking = BookingWithPickup();
        var online = Guid.NewGuid();
        var offline = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(online, 14.60m, 120.99m, 1.0),
                new(offline, 14.61m, 120.98m, 0.5),
            });
        UsersAre(Driver(online), Driver(offline, online: false));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(online, result[0].DriverId);
    }

    [Fact]
    public async Task Offline_drivers_are_excluded_from_fallback_scan()
    {
        var booking = BookingWithPickup();
        var offline = Driver(Guid.NewGuid(), online: false);

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(offline);

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task Not_onboarded_drivers_are_excluded_from_h3_candidates()
    {
        var booking = BookingWithPickup();
        var approved = Guid.NewGuid();
        var notApproved = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(approved, 14.60m, 120.99m, 1.0),
                new(notApproved, 14.61m, 120.98m, 0.5),
            });
        UsersAre(Driver(approved), Driver(notApproved, onboarded: false));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(approved, result[0].DriverId);
    }

    [Fact]
    public async Task Not_onboarded_drivers_are_excluded_from_fallback_scan()
    {
        var booking = BookingWithPickup();
        var notApproved = Driver(Guid.NewGuid(), onboarded: false);

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(notApproved);

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task Proximity_radius_filters_h3_candidates()
    {
        var booking = BookingWithPickup();
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(near, 14.60m, 120.99m, 1.0),
                new(far, 14.70m, 121.05m, 5.0),
            });
        UsersAre(Driver(near), Driver(far));

        var result = await CreateService(proximityRadiusKm: 2).GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(near, result[0].DriverId);
    }

    [Fact]
    public async Task Empty_geo_index_falls_back_to_full_scan()
    {
        var booking = BookingWithPickup();
        var driverId = Guid.NewGuid();
        var driver = Driver(driverId);
        driver.CurrentLatitude = 14.60m;
        driver.CurrentLongitude = 120.99m;

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(driver);
        _distanceService.CalculateDistancesToPointAsync(
                Arg.Any<decimal>(), Arg.Any<decimal>(),
                Arg.Any<IEnumerable<(Guid, decimal, decimal)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, decimal)> { (driverId, 1.2m) });

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(driverId, result[0].DriverId);
        Assert.Equal(1.2m, result[0].DistanceKm);
    }

    [Fact]
    public async Task Fallback_includes_drivers_without_location_when_no_radius_is_set()
    {
        var booking = BookingWithPickup();
        var noLocationDriver = Driver(Guid.NewGuid()); // CurrentLatitude/Longitude null

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(noLocationDriver);

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Null(result[0].DistanceKm);
    }

    [Fact]
    public async Task Search_radius_starts_narrow_for_a_fresh_booking()
    {
        var booking = BookingWithPickup(); // CreatedAt ~ now

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre();

        await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        // Default H3InitialSearchRadiusKm=3 -> 3 rings for a booking with ~0 elapsed minutes.
        await _geoIndex.Received(1).FindNearestDriversAsync(
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Is<int?>(r => r == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Search_radius_widens_to_the_max_as_the_booking_approaches_the_give_up_window()
    {
        var booking = BookingWithPickup();
        SetCreatedAt(booking, DateTime.UtcNow.AddMinutes(-10)); // default MaxBroadcastMinutes=10

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre();

        await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        // Default H3MaxSearchRadiusKm=10 -> 10 rings once elapsed time reaches the window.
        await _geoIndex.Received(1).FindNearestDriversAsync(
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Is<int?>(r => r == 10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mode_can_search_wider_from_the_first_attempt()
    {
        // "Aggressive from t=0" is not a separate code path — it is a larger initial radius. A
        // fresh On-Demand booking casts the net a Regular one only reaches after minutes of waiting.
        var booking = BookingWithPickup(DeliveryMode.OnDemand);

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre();

        await CreateService(extraSettings: ("BookingSettings:DeliveryModes:OnDemand:H3InitialSearchRadiusKm", "7"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        await _geoIndex.Received(1).FindNearestDriversAsync(
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Is<int?>(r => r == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mode_override_does_not_widen_the_other_modes()
    {
        // The same configuration, a Regular booking: still the flat initial radius.
        var booking = BookingWithPickup();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre();

        await CreateService(extraSettings: ("BookingSettings:DeliveryModes:OnDemand:H3InitialSearchRadiusKm", "7"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        await _geoIndex.Received(1).FindNearestDriversAsync(
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Is<int?>(r => r == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_wider_give_up_window_stretches_the_radius_ramp_rather_than_shortcutting_it()
    {
        // MaxBroadcastMinutes is the denominator of the ramp as well as the give-up window. If the
        // two disagree, a Pooling booking hits its widest ring long before it stops searching and
        // spends the rest of the window re-querying it.
        var booking = BookingWithPickup(DeliveryMode.Pooling);
        SetCreatedAt(booking, DateTime.UtcNow.AddMinutes(-10));

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre();

        await CreateService(extraSettings: ("BookingSettings:DeliveryModes:Pooling:MaxBroadcastMinutes", "25"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        // 10 of 25 minutes elapsed: 3 + (10 - 3) * 0.4 = 5.8 -> 5 rings, not the max of 10 a
        // Regular booking would have reached by now.
        await _geoIndex.Received(1).FindNearestDriversAsync(
            Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Is<int?>(r => r == 5), Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ ranking bias

    [Fact]
    public void A_zero_bonus_is_exactly_the_proximity_score()
    {
        // Not "approximately": adding 0.0 to a finite double is exact. This is what makes
        // "Regular ranking is unchanged" checkable rather than a claim.
        foreach (var km in new decimal?[] { null, 0m, 0.4m, 1m, 7.25m, 100m })
            Assert.Equal(AvailableDriver.ProximityScore(km), AvailableDriver.RankingScore(km, 0));
    }

    [Fact]
    public async Task A_regular_booking_asks_for_no_bias_data_at_all()
    {
        // Inert means inert: no held-legs query, no responsiveness query, no extra round trip on
        // the hot path for the mode that is 100% of today's traffic.
        var booking = BookingWithPickup();
        var driver = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(driver, 14.60m, 120.99m, 1.0) });
        UsersAre(Driver(driver));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        await _bookingRepo.DidNotReceive().GetHeldLegsForDriversAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _offerRepo.DidNotReceive().GetResponseStatsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.Equal(AvailableDriver.ProximityScore(1.0m), result[0].Score);
    }

    [Fact]
    public async Task An_unweighted_pooling_booking_issues_no_query_and_ranks_as_before()
    {
        // How this ships. The lever exists but costs nothing until someone sets a weight.
        var booking = BookingWithPickup(DeliveryMode.Pooling);
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(far, 14.65m, 121.0m, 5.0), new(near, 14.60m, 120.99m, 1.0) });
        UsersAre(Driver(near), Driver(far));

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        await _bookingRepo.DidNotReceive().GetHeldLegsForDriversAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        Assert.Equal(near, result[0].DriverId);
        Assert.Equal(AvailableDriver.ProximityScore(1.0m), result[0].Score);
    }

    [Fact]
    public async Task A_weighted_pooling_booking_fetches_held_legs_exactly_once()
    {
        // The N+1 guard. Scoring is O(candidates x held legs) inside a 15-second tick, so the
        // difference between one query and one-per-driver is the difference between a bias and an
        // outage.
        var booking = BookingWithPickup(DeliveryMode.Pooling);
        var drivers = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(drivers.Select((d, i) => new DriverGeoCandidate(d, 14.60m, 120.99m, 1.0 + i)).ToList());
        UsersAre(drivers.Select(d => Driver(d)).ToArray());
        _bookingRepo.GetHeldLegsForDriversAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DriverHeldLeg>());

        await PoolingService(weight: 0.35).GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        await _bookingRepo.Received(1).GetHeldLegsForDriversAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_compatible_busy_driver_can_outrank_a_slightly_closer_idle_one_when_pooling()
    {
        // The whole point of the lever. `busy` is further from pickup but already heading past it.
        var booking = BookingWithPickup(DeliveryMode.Pooling, lat: 14.60m, lng: 120.98m);
        var idle = Guid.NewGuid();
        var busy = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(idle, 14.60m, 120.98m, 0.5),   // closer
                new(busy, 14.59m, 120.98m, 1.5),   // further, but its held leg is just past the dropoff
            });
        UsersAre(Driver(idle), Driver(busy));
        _bookingRepo.GetHeldLegsForDriversAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new DriverHeldLeg(busy, Guid.NewGuid(), 14.73m, 121.09m) });

        var pooled = await PoolingService(weight: 0.35).GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(busy, pooled[0].DriverId);
        Assert.Equal(idle, pooled[1].DriverId);
    }

    [Fact]
    public async Task The_same_pair_ranks_by_proximity_when_the_booking_is_regular()
    {
        // Identical fixture, Regular mode: the closer driver wins and the held leg is never read.
        // Without this, the test above only proves the bias fires, not that it is scoped to a mode.
        var booking = BookingWithPickup(lat: 14.60m, lng: 120.98m);
        var idle = Guid.NewGuid();
        var busy = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(idle, 14.60m, 120.98m, 0.5),
                new(busy, 14.59m, 120.98m, 1.5),
            });
        UsersAre(Driver(idle), Driver(busy));
        _bookingRepo.GetHeldLegsForDriversAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new DriverHeldLeg(busy, Guid.NewGuid(), 14.73m, 121.09m) });

        var result = await PoolingService(weight: 0.35).GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(idle, result[0].DriverId);
        await _bookingRepo.DidNotReceive().GetHeldLegsForDriversAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pooling_booking_whose_drivers_hold_nothing_ranks_exactly_like_regular()
    {
        var booking = BookingWithPickup(DeliveryMode.Pooling);
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(far, 14.65m, 121.0m, 5.0), new(near, 14.60m, 120.99m, 1.0) });
        UsersAre(Driver(near), Driver(far));
        _bookingRepo.GetHeldLegsForDriversAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DriverHeldLeg>());

        var result = await PoolingService(weight: 0.35).GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(near, result[0].DriverId);
        Assert.Equal(AvailableDriver.ProximityScore(1.0m), result[0].Score);
        Assert.Equal(AvailableDriver.ProximityScore(5.0m), result[1].Score);
    }

    [Fact]
    public async Task A_weighted_on_demand_booking_prefers_the_more_responsive_driver()
    {
        var booking = BookingWithPickup(DeliveryMode.OnDemand);
        var reliable = Guid.NewGuid();
        var flaky = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(flaky, 14.60m, 120.99m, 1.0),      // closer
                new(reliable, 14.62m, 121.0m, 1.6),    // further, but actually takes jobs
            });
        UsersAre(Driver(reliable), Driver(flaky));
        _offerRepo.GetResponseStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, OfferResponseStats>
            {
                [reliable] = new(20, 19),
                [flaky] = new(20, 1),
            });

        var result = await CreateService(extraSettings:
                ("BookingSettings:DeliveryModes:OnDemand:QualityScoreWeight", "0.35"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(reliable, result[0].DriverId);
        await _offerRepo.Received(1).GetResponseStatsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_driver_is_not_ranked_into_invisibility()
    {
        // No history at all versus a driver who declines almost everything: the newcomer's neutral
        // 0.5 has to beat the flaky driver's 0.05, or a new driver never gets a first good offer.
        var booking = BookingWithPickup(DeliveryMode.OnDemand);
        var newcomer = Guid.NewGuid();
        var flaky = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(flaky, 14.60m, 120.99m, 2.0),
                new(newcomer, 14.60m, 120.99m, 2.0),   // same distance, so only the bias separates them
            });
        UsersAre(Driver(newcomer), Driver(flaky));
        _offerRepo.GetResponseStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, OfferResponseStats> { [flaky] = new(20, 1) });

        var result = await CreateService(extraSettings:
                ("BookingSettings:DeliveryModes:OnDemand:QualityScoreWeight", "0.35"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(newcomer, result[0].DriverId);
    }

    [Fact]
    public async Task The_bias_applies_on_the_fallback_scan_too()
    {
        // The two candidate paths used to rank with separately written code. Which one runs depends
        // on whether Redis had candidates, so a bias on only one would look like a flaky market.
        var booking = BookingWithPickup(DeliveryMode.OnDemand);
        var reliable = Guid.NewGuid();
        var flaky = Guid.NewGuid();

        var reliableUser = Driver(reliable);
        reliableUser.CurrentLatitude = 14.62m;
        reliableUser.CurrentLongitude = 121.0m;
        var flakyUser = Driver(flaky);
        flakyUser.CurrentLatitude = 14.60m;
        flakyUser.CurrentLongitude = 120.99m;

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(reliableUser, flakyUser);
        _distanceService.CalculateDistancesToPointAsync(
                Arg.Any<decimal>(), Arg.Any<decimal>(),
                Arg.Any<IEnumerable<(Guid, decimal, decimal)>>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, decimal)> { (flaky, 1.0m), (reliable, 1.6m) });
        _offerRepo.GetResponseStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, OfferResponseStats>
            {
                [reliable] = new(20, 19),
                [flaky] = new(20, 1),
            });

        var result = await CreateService(extraSettings:
                ("BookingSettings:DeliveryModes:OnDemand:QualityScoreWeight", "0.35"))
            .GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(reliable, result[0].DriverId);
    }

    /// <summary>A service with the pooling bias switched on at <paramref name="weight"/>.</summary>
    private DriverAvailabilityService PoolingService(double weight) => CreateService(extraSettings:
    [
        ("BookingSettings:DeliveryModes:Pooling:PoolingScoreWeight", weight.ToString(CultureInfo.InvariantCulture)),
        ("BookingSettings:DeliveryModes:Pooling:MaxDetourKm", "4.0"),
    ]);

    [Fact]
    public async Task Unknown_booking_returns_empty()
    {
        _bookingRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Booking?)null);

        var result = await CreateService().GetAvailableDriversAsync(Guid.NewGuid(), Array.Empty<string>());

        Assert.Empty(result);
    }

    // --- The wallet gate (issue #102) ---------------------------------------------------------
    //
    // Every booking is paid in cash, so the driver collects the whole fare and we sweep our
    // commission out of their wallet afterwards. Offering a job to a driver who cannot cover that
    // fee is how an uncollectable debt is created, so the check belongs here, before the offer.

    [Fact]
    public async Task A_driver_who_cannot_pay_the_commission_is_not_offered_the_booking()
    {
        var booking = BookingWithPickup(DeliveryMode.Regular);
        var funded = Guid.NewGuid();
        var broke = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>
            {
                new(broke, 14.60m, 120.99m, 1.0),      // closer, and still not offered
                new(funded, 14.62m, 121.0m, 5.0),
            });
        UsersAre(Driver(funded), Driver(broke));
        _driversWhoCanPayCommission = [funded];

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(funded, result[0].DriverId);
    }

    [Fact]
    public async Task The_wallet_gate_applies_to_the_fallback_scan_too()
    {
        // Both candidate paths must gate identically: which one runs depends on whether Redis had
        // candidates, so a gate on only one would let a driver through based on infrastructure.
        var booking = BookingWithPickup(DeliveryMode.Regular);
        var funded = Driver(Guid.NewGuid());
        var broke = Driver(Guid.NewGuid());

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate>());
        UsersAre(funded, broke);
        _driversWhoCanPayCommission = [Guid.Parse(funded.Id)];

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(Guid.Parse(funded.Id), result[0].DriverId);
    }

    [Fact]
    public async Task The_whole_candidate_set_is_checked_in_one_call()
    {
        // This runs in front of every booking. A query per candidate would put the wallet check in
        // the way of dispatch itself.
        var booking = BookingWithPickup(DeliveryMode.Regular);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(a, 14.60m, 120.99m, 1.0), new(b, 14.62m, 121.0m, 5.0) });
        UsersAre(Driver(a), Driver(b));

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDriversEligibleForCashJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => ((GetDriversEligibleForCashJobQuery)call[0]).DriverIds.ToList());

        var service = new DriverAvailabilityService(
            _bookingRepo, _offerRepo, _userManager, mediator, _distanceService, _geoIndex,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BookingSettings:MaxOffersPerBroadcast"] = "15",
                ["BookingSettings:ProximityRadiusKm"] = "0",
            }).Build(),
            NullLogger<DriverAvailabilityService>.Instance);

        var result = await service.GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Equal(2, result.Count);
        await mediator.Received(1).Send(
            Arg.Is<GetDriversEligibleForCashJobQuery>(q => q.DriverIds.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_booking_nobody_can_afford_reaches_nobody()
    {
        var booking = BookingWithPickup(DeliveryMode.Regular);
        var broke = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(broke, 14.60m, 120.99m, 1.0) });
        UsersAre(Driver(broke));
        _driversWhoCanPayCommission = [];

        var result = await CreateService().GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Empty(result);
    }

    /// <summary>
    /// Losing dispatch entirely because the wallet query failed would be a far worse outcome than a
    /// commission we have to chase — so a failure here offers to everyone rather than nobody.
    /// </summary>
    [Fact]
    public async Task A_failing_wallet_check_does_not_stop_dispatch()
    {
        var booking = BookingWithPickup(DeliveryMode.Regular);
        var driver = Guid.NewGuid();

        _geoIndex.FindNearestDriversAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<DriverGeoCandidate> { new(driver, 14.60m, 120.99m, 1.0) });
        UsersAre(Driver(driver));

        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDriversEligibleForCashJobQuery>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Guid>>(_ => throw new InvalidOperationException("wallet module down"));

        var service = new DriverAvailabilityService(
            _bookingRepo, _offerRepo, _userManager, mediator, _distanceService, _geoIndex,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BookingSettings:MaxOffersPerBroadcast"] = "15",
                ["BookingSettings:ProximityRadiusKm"] = "0",
            }).Build(),
            NullLogger<DriverAvailabilityService>.Instance);

        var result = await service.GetAvailableDriversAsync(booking.Id, Array.Empty<string>());

        Assert.Single(result);
        Assert.Equal(driver, result[0].DriverId);
    }
}
