using BeeLogistics.Modules.Map.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Map;

/// <summary>
/// The singleton that makes the sanitizer's rate limit and teleport check real. Time is passed in
/// rather than read from the clock, so refill and expiry are testable without sleeping.
/// </summary>
public class DriverLocationStateTrackerTests
{
    private static readonly Guid DriverId = Guid.NewGuid();

    private static DriverLocationStateTracker Tracker(Dictionary<string, string?>? config = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
        NullLogger<DriverLocationStateTracker>.Instance);

    // ---- Token bucket -----------------------------------------------------------------------

    [Fact]
    public void Allows_a_full_burst_then_refuses()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
            Assert.True(tracker.TryConsumeUpdateAllowance(DriverId, now, refillPerSecond: 1, burstCapacity: 10), $"Token {i} should have been available");

        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, now, refillPerSecond: 1, burstCapacity: 10));
    }

    [Fact]
    public void Refills_over_time()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
            tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 10);

        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 10));

        // One second later, one token is back.
        Assert.True(tracker.TryConsumeUpdateAllowance(DriverId, now.AddSeconds(1), 1, 10));
        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, now.AddSeconds(1), 1, 10));
    }

    [Fact]
    public void Refill_is_capped_at_the_burst_capacity()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;

        tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 5);

        // An hour of idling must not bank 3600 tokens.
        var later = now.AddHours(1);
        for (var i = 0; i < 5; i++)
            Assert.True(tracker.TryConsumeUpdateAllowance(DriverId, later, 1, 5));

        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, later, 1, 5));
    }

    [Fact]
    public void Budgets_are_per_driver()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;
        var other = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 5);

        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 5));
        Assert.True(tracker.TryConsumeUpdateAllowance(other, now, 1, 5));
    }

    // ---- Baseline ---------------------------------------------------------------------------

    [Fact]
    public void Returns_no_baseline_for_an_unknown_driver()
    {
        Assert.Null(Tracker().GetBaseline(DriverId, DateTime.UtcNow, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Returns_a_recorded_baseline()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;

        tracker.RecordAccepted(DriverId, 16.6159m, 120.3166m, now, now);

        var baseline = tracker.GetBaseline(DriverId, now, TimeSpan.FromMinutes(5));

        Assert.NotNull(baseline);
        Assert.Equal(16.6159m, baseline!.Value.Latitude);
        Assert.Equal(120.3166m, baseline.Value.Longitude);
    }

    [Fact]
    public void Expires_a_stale_baseline()
    {
        var tracker = Tracker();
        var recordedAt = DateTime.UtcNow.AddMinutes(-10);

        tracker.RecordAccepted(DriverId, 16.6159m, 120.3166m, recordedAt, recordedAt);

        Assert.Null(tracker.GetBaseline(DriverId, DateTime.UtcNow, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// A burst flush can deliver points out of order. The newest reading has to win, or a late
    /// older point rewinds the baseline and makes the following live point look like a jump.
    /// </summary>
    [Fact]
    public void Older_reading_does_not_rewind_the_baseline()
    {
        var tracker = Tracker();
        var now = DateTime.UtcNow;

        tracker.RecordAccepted(DriverId, 16.6159m, 120.3166m, now, now);
        tracker.RecordAccepted(DriverId, 14.5995m, 120.9842m, now.AddSeconds(-30), now);

        var baseline = tracker.GetBaseline(DriverId, now, TimeSpan.FromMinutes(5));

        Assert.Equal(16.6159m, baseline!.Value.Latitude);
    }

    // ---- Retention --------------------------------------------------------------------------

    /// <summary>
    /// Now that this state outlives a single message, the dictionary would otherwise grow once per
    /// driver ever seen and never shrink. Forgetting an idle driver is safe: they resume with a
    /// full bucket and no baseline, the same position a freshly restarted process is in.
    /// </summary>
    [Fact]
    public void Evicts_drivers_idle_beyond_the_retention_window()
    {
        var tracker = Tracker(new Dictionary<string, string?> { ["Location:StateRetentionMinutes"] = "10" });
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
            tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 5);

        Assert.False(tracker.TryConsumeUpdateAllowance(DriverId, now, 1, 5));

        // A later call from any driver triggers the sweep; the idle driver's entry goes with it, so
        // their bucket is full again rather than still exhausted.
        var later = now.AddMinutes(20);
        tracker.TryConsumeUpdateAllowance(Guid.NewGuid(), later, 1, 5);

        Assert.True(tracker.TryConsumeUpdateAllowance(DriverId, later, 1, 5));
        Assert.Null(tracker.GetBaseline(DriverId, later, TimeSpan.FromMinutes(5)));
    }
}
