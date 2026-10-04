using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// The sanitizer is now shared by MQTT and HTTP (GitLab #37), so backfilled points run through the
/// same validation live points do. Two of its checks are hostile to backdated data by construction
/// and are relaxed for historical points only:
///
///   - the maximum-age limit, which a buffered point exceeds by definition, and
///   - the per-driver rate limit, which a backlog arriving in one burst would trip immediately.
///
/// Everything else still applies. Without these two exemptions the batch endpoint would reject the
/// very data it exists to rescue; with them applied too broadly, live GPS spoofing gets a free pass.
/// These tests pin both edges.
/// </summary>
public class LocationSanitizerHistoricalTests
{
    private readonly IDistanceCalculationService _distance = Substitute.For<IDistanceCalculationService>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();

    /// <summary>
    /// One tracker shared by every sanitizer these tests build, mirroring the singleton
    /// registration. Each Sanitizer() call returns a *new* sanitizer, which is what production does
    /// per message - so any state that survives between them is surviving for the right reason.
    /// </summary>
    private readonly DriverLocationStateTracker _stateTracker = new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
        NullLogger<DriverLocationStateTracker>.Instance);

    private static readonly Guid DriverId = Guid.NewGuid();

    public LocationSanitizerHistoricalTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.FindByIdAsync(DriverId.ToString())
            .Returns(new ApplicationUser { Id = DriverId.ToString(), IsActive = true, Role = "Driver" });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(UserManager<ApplicationUser>)).Returns(userManager);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);

        // Consecutive points in these tests are metres apart, well under the teleport threshold.
        _distance.CalculateDistanceAsync(
                Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(0.01m);
    }

    private LocationSanitizer Sanitizer(Dictionary<string, string?>? config = null) => new(
        _scopeFactory,
        new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
        NullLogger<LocationSanitizer>.Instance,
        _distance,
        _stateTracker);

    private static DriverLocationUpdateDto Point(DateTime timestamp, decimal lat = 16.6159m, decimal lng = 120.3166m) => new()
    {
        DriverId = DriverId,
        Latitude = lat,
        Longitude = lng,
        Speed = 40m,
        Heading = 90m,
        Timestamp = timestamp,
        DeviceId = "test-device"
    };

    // ---- Age limit --------------------------------------------------------------------------

    [Fact]
    public async Task Old_point_is_rejected_when_live()
    {
        var result = await Sanitizer().SanitizeAsync(Point(DateTime.UtcNow.AddMinutes(-30)), DriverId);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.InvalidTimestamp, result.RejectionReason);
    }

    [Fact]
    public async Task Old_point_is_accepted_when_historical()
    {
        var result = await Sanitizer().SanitizeAsync(Point(DateTime.UtcNow.AddMinutes(-30)), DriverId, isHistorical: true);

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// Being backdated is expected; being dated after the moment we received it is not. The future
    /// check stays on for historical points.
    /// </summary>
    [Fact]
    public async Task Future_point_is_rejected_even_when_historical()
    {
        var result = await Sanitizer().SanitizeAsync(Point(DateTime.UtcNow.AddMinutes(10)), DriverId, isHistorical: true);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.InvalidTimestamp, result.RejectionReason);
    }

    // ---- Rate limit -------------------------------------------------------------------------

    /// <summary>
    /// The regression test for the bug this whole tracker exists to fix. Production resolves a
    /// fresh sanitizer per message, so any state kept in sanitizer fields is empty on arrival and
    /// the rate limit silently never fires. Separate instances here must still share a budget.
    /// </summary>
    [Fact]
    public async Task Rate_limit_state_survives_across_sanitizer_instances()
    {
        var config = new Dictionary<string, string?> { ["Location:UpdateBurstAllowance"] = "3" };

        for (var i = 0; i < 3; i++)
        {
            // A brand-new sanitizer each time, exactly as the DI container hands one out per message.
            var result = await Sanitizer(config).SanitizeAsync(Point(DateTime.UtcNow), DriverId);
            Assert.True(result.IsValid, $"Point {i} should have been within the burst allowance");
        }

        var overBudget = await Sanitizer(config).SanitizeAsync(Point(DateTime.UtcNow), DriverId);

        Assert.False(overBudget.IsValid);
        Assert.Equal(SanitizationRejectionReason.RateLimitExceeded, overBudget.RejectionReason);
    }

    /// <summary>
    /// The teleport baseline has to survive the same way, or the anomaly check is equally dead.
    /// </summary>
    [Fact]
    public async Task Teleport_baseline_survives_across_sanitizer_instances()
    {
        // 500 km apart, well past the 100 km teleport threshold, at an impossible implied speed.
        _distance.CalculateDistanceAsync(
                Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(500m);

        var first = await Sanitizer().SanitizeAsync(Point(DateTime.UtcNow.AddSeconds(-3)), DriverId);
        Assert.True(first.IsValid);

        var teleported = await Sanitizer().SanitizeAsync(Point(DateTime.UtcNow, lat: 14.5995m, lng: 120.9842m), DriverId);

        Assert.False(teleported.IsValid);
        Assert.Equal(SanitizationRejectionReason.AnomalyDetected, teleported.RejectionReason);
    }

    /// <summary>
    /// The reason the limiter is a token bucket rather than a minimum interval. The driver app
    /// publishes its buffer to MQTT with Promise.all, so up to BATCH_SIZE_LIMIT (10) points land in
    /// the same instant. A one-per-second gate would have dropped nine of every ten live points the
    /// moment this check started working.
    /// </summary>
    [Fact]
    public async Task Simultaneous_mqtt_flush_of_ten_points_is_fully_accepted()
    {
        var now = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            var result = await Sanitizer().SanitizeAsync(Point(now), DriverId);
            Assert.True(result.IsValid, $"Point {i} of a normal MQTT flush was rejected: {string.Join("; ", result.Errors)}");
        }
    }

    /// <summary>
    /// Burst tolerance must not become no limit at all - a sustained flood still has to be capped.
    /// </summary>
    [Fact]
    public async Task Sustained_flood_beyond_the_burst_allowance_is_rejected()
    {
        var now = DateTime.UtcNow;
        var rejected = false;

        for (var i = 0; i < 40; i++)
        {
            var result = await Sanitizer().SanitizeAsync(Point(now), DriverId);
            if (!result.IsValid)
            {
                Assert.Equal(SanitizationRejectionReason.RateLimitExceeded, result.RejectionReason);
                rejected = true;
                break;
            }
        }

        Assert.True(rejected, "A 40-point flood should have exhausted the burst allowance");
    }

    /// <summary>
    /// A flushed backlog arrives all at once. If the rate limit applied, only the first point would
    /// survive and the rest of the trail would be silently discarded.
    /// </summary>
    [Fact]
    public async Task Burst_is_not_rate_limited_when_historical()
    {
        var sanitizer = Sanitizer();
        var baseTime = DateTime.UtcNow.AddMinutes(-30);

        for (var i = 0; i < 10; i++)
        {
            var result = await sanitizer.SanitizeAsync(Point(baseTime.AddSeconds(i * 3)), DriverId, isHistorical: true);
            Assert.True(result.IsValid, $"Point {i} was rejected: {string.Join("; ", result.Errors)}");
        }
    }

    /// <summary>
    /// Backfilling must not consume the live budget: a historical burst followed immediately by a
    /// live point has to leave that live point acceptable.
    /// </summary>
    [Fact]
    public async Task Historical_points_do_not_consume_the_live_rate_limit()
    {
        var sanitizer = Sanitizer();
        var baseTime = DateTime.UtcNow.AddMinutes(-30);

        for (var i = 0; i < 5; i++)
            await sanitizer.SanitizeAsync(Point(baseTime.AddSeconds(i * 3)), DriverId, isHistorical: true);

        var live = await sanitizer.SanitizeAsync(Point(DateTime.UtcNow), DriverId);

        Assert.True(live.IsValid);
    }

    /// <summary>
    /// The failure mode that makes a persistent baseline dangerous if it never expires: a driver
    /// goes offline, travels a long way, and comes back. Measured against the stale position the
    /// first point back looks impossible - and because a rejected point never becomes the new
    /// baseline, every subsequent point fails identically and the driver is wedged for good.
    ///
    /// An expired baseline has to mean "no opinion", not "reject".
    /// </summary>
    [Fact]
    public async Task Driver_returning_after_an_offline_gap_is_not_permanently_wedged()
    {
        var config = new Dictionary<string, string?>
        {
            ["Location:MaxLocationAgeMinutes"] = "60",   // let the old point through age validation
            ["Location:BaselineMaxAgeMinutes"] = "1"     // but expire it as a teleport reference
        };

        _distance.CalculateDistanceAsync(
                Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(500m);

        var beforeGap = await Sanitizer(config).SanitizeAsync(Point(DateTime.UtcNow.AddMinutes(-10)), DriverId);
        Assert.True(beforeGap.IsValid);

        // Back online, 500 km away, after a gap longer than the baseline lifetime.
        var afterGap = await Sanitizer(config).SanitizeAsync(Point(DateTime.UtcNow, lat: 14.5995m, lng: 120.9842m), DriverId);

        Assert.True(afterGap.IsValid, $"Returning driver was rejected: {string.Join("; ", afterGap.Errors)}");
    }

    // ---- Everything else still applies -------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_island_is_rejected_in_both_modes(bool isHistorical)
    {
        var timestamp = isHistorical ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow;

        var result = await Sanitizer().SanitizeAsync(Point(timestamp, lat: 0m, lng: 0m), DriverId, isHistorical);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.InvalidCoordinates, result.RejectionReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Out_of_range_latitude_is_rejected_in_both_modes(bool isHistorical)
    {
        var timestamp = isHistorical ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow;

        var result = await Sanitizer().SanitizeAsync(Point(timestamp, lat: 999m), DriverId, isHistorical);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.InvalidCoordinates, result.RejectionReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Impossible_speed_is_rejected_in_both_modes(bool isHistorical)
    {
        var timestamp = isHistorical ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow;
        var point = Point(timestamp) with { Speed = 5000m };

        var result = await Sanitizer().SanitizeAsync(point, DriverId, isHistorical);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.InvalidSpeed, result.RejectionReason);
    }

    /// <summary>
    /// The HTTP endpoints authenticate the caller but do not check what kind of account it is -
    /// the sanitizer is what stops a customer posting themselves onto the driver map. It has to
    /// keep doing that for backfilled points too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_driver_is_rejected_in_both_modes(bool isHistorical)
    {
        var customerId = Guid.NewGuid();
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var userManager = Substitute.For<UserManager<ApplicationUser>>(
            store, null!, null!, null!, null!, null!, null!, null!, null!);
        userManager.FindByIdAsync(customerId.ToString())
            .Returns(new ApplicationUser { Id = customerId.ToString(), IsActive = true, Role = "Customer" });

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(UserManager<ApplicationUser>)).Returns(userManager);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);

        var timestamp = isHistorical ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow;

        var result = await Sanitizer().SanitizeAsync(Point(timestamp), customerId, isHistorical);

        Assert.False(result.IsValid);
        Assert.Equal(SanitizationRejectionReason.DriverInactive, result.RejectionReason);
    }
}
