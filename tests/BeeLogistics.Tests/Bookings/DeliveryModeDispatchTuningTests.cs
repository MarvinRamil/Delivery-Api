using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// Per-mode dispatch tuning. The behaviour these tests exist to protect is the <i>absence</i> of a
/// change: every knob is now looked up through <see cref="DeliveryModeSettings"/>, and if that
/// lookup ever stops falling back to the flat <c>BookingSettings</c> key, every Regular booking on
/// the platform silently changes its search radius, its pulse cadence, its offer lifetime and the
/// point at which it gives up — all at once, with no error.
///
/// So the Regular cases here matter more than the On-Demand ones.
/// </summary>
public class DeliveryModeDispatchTuningTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    // --- Resolution order ---

    [Fact]
    public void A_mode_override_wins_over_the_flat_key()
    {
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:Pooling:MaxBroadcastMinutes", "25"));

        Assert.Equal(25, DeliveryModeSettings.Value(config, DeliveryMode.Pooling, "MaxBroadcastMinutes", 10));
    }

    [Fact]
    public void A_mode_with_no_override_falls_back_to_the_flat_key()
    {
        // The no-regression guarantee, stated directly: Pooling is tuned, Regular is not, and
        // Regular must still read the value it always read.
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:Pooling:MaxBroadcastMinutes", "25"));

        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.Regular, "MaxBroadcastMinutes", 10));
        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.OnDemand, "MaxBroadcastMinutes", 10));
    }

    [Fact]
    public void With_nothing_configured_every_mode_gets_the_callers_default()
    {
        foreach (var mode in Enum.GetValues<DeliveryMode>())
            Assert.Equal(42, DeliveryModeSettings.Value(Config(), mode, "MaxBroadcastMinutes", 42));
    }

    [Fact]
    public void One_overridden_knob_does_not_drag_its_neighbours_with_it()
    {
        // Overrides are deltas, not a replacement block: a mode that only widens its radius must
        // keep the flat cadence rather than silently reverting the other knobs to code defaults.
        var config = Config(
            ("BookingSettings:PulseIntervalFreshSeconds", "10"),
            ("BookingSettings:H3InitialSearchRadiusKm", "3"),
            ("BookingSettings:DeliveryModes:OnDemand:H3InitialSearchRadiusKm", "7"));

        Assert.Equal(7m, DeliveryModeSettings.Value(config, DeliveryMode.OnDemand, "H3InitialSearchRadiusKm", 3m));
        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.OnDemand, "PulseIntervalFreshSeconds", 10));
    }

    [Fact]
    public void A_blank_value_is_not_a_configured_value()
    {
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:OnDemand:MaxBroadcastMinutes", ""));

        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.OnDemand, "MaxBroadcastMinutes", 10));
    }

    [Fact]
    public void A_malformed_value_falls_through_instead_of_throwing()
    {
        // Every caller of this sits on the dispatch path — inside the Hangfire pulse tick or the
        // broadcast consumer. GetValue<int> throws on an unconvertible value, so without the
        // fallback one bad entry would stop dispatch for every booking, not just mis-tune one knob.
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:OnDemand:MaxBroadcastMinutes", "twenty-five"));

        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.OnDemand, "MaxBroadcastMinutes", 10));
    }

    [Fact]
    public void A_malformed_flat_value_falls_through_to_the_default()
    {
        var config = Config(("BookingSettings:MaxBroadcastMinutes", "1O"));  // letter O

        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.Regular, "MaxBroadcastMinutes", 10));
    }

    [Fact]
    public void An_empty_mode_section_reads_as_unconfigured()
    {
        // "Regular": {} is written in config on purpose, to document that the mode exists and is
        // tuned like everything else. It must not shadow the flat key.
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:Regular:SomethingElse", "1"));

        Assert.Equal(10, DeliveryModeSettings.Value(config, DeliveryMode.Regular, "MaxBroadcastMinutes", 10));
    }

    // --- Offer lifetime ---

    [Fact]
    public void An_offer_TTL_honours_the_mode()
    {
        var config = Config(
            ("BookingSettings:OfferExpirationFewDriversMinutes", "5"),
            ("BookingSettings:DeliveryModes:Pooling:OfferExpirationFewDriversMinutes", "8"));

        var regular = OfferExpiry.Calculate(config, availableDriverCount: 1, maxOffersPerBroadcast: 15);
        var pooling = OfferExpiry.Calculate(config, 1, 15, DeliveryMode.Pooling);

        Assert.InRange(regular, DateTime.UtcNow.AddMinutes(4.9), DateTime.UtcNow.AddMinutes(5.1));
        Assert.InRange(pooling, DateTime.UtcNow.AddMinutes(7.9), DateTime.UtcNow.AddMinutes(8.1));
    }

    [Fact]
    public void An_offer_TTL_still_floors_at_a_minute_however_it_is_configured()
    {
        // Two separate expiry paths apply a 30-second grace, so a 1-minute TTL is already only
        // ~30 seconds of real decision time. A mode must not be able to configure its way below it.
        var config = Config(("BookingSettings:DeliveryModes:OnDemand:OfferExpirationManyDriversMinutes", "0"));

        var expiry = OfferExpiry.Calculate(config, availableDriverCount: 20, maxOffersPerBroadcast: 15,
            mode: DeliveryMode.OnDemand);

        Assert.InRange(expiry, DateTime.UtcNow.AddSeconds(59), DateTime.UtcNow.AddMinutes(1.1));
    }

    [Fact]
    public void An_unconfigured_offer_TTL_keeps_the_documented_defaults()
    {
        var many = OfferExpiry.Calculate(Config(), availableDriverCount: 20, maxOffersPerBroadcast: 15);
        var few = OfferExpiry.Calculate(Config(), availableDriverCount: 1, maxOffersPerBroadcast: 15);

        Assert.InRange(many, DateTime.UtcNow.AddMinutes(1.9), DateTime.UtcNow.AddMinutes(2.1));
        Assert.InRange(few, DateTime.UtcNow.AddMinutes(4.9), DateTime.UtcNow.AddMinutes(5.1));
    }

    // --- Give-up window ---

    [Fact]
    public void The_give_up_window_honours_the_mode()
    {
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:DeliveryModes:Pooling:MaxBroadcastMinutes", "25"));
        var createdAt = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

        var regular = BookingBroadcastQueueService.ComputeGiveUpDeadline(createdAt, createdAt, config);
        var pooling = BookingBroadcastQueueService.ComputeGiveUpDeadline(
            createdAt, createdAt, config, DeliveryMode.Pooling);

        Assert.Equal(createdAt.AddMinutes(10), regular);
        Assert.Equal(createdAt.AddMinutes(25), pooling);
    }

    [Fact]
    public void The_scheduled_lead_time_still_wins_when_it_is_later_whatever_the_mode()
    {
        // #50's rule is orthogonal to modes: a booking for hours from now keeps broadcasting until
        // shortly before its slot, no matter how short its on-demand window is.
        var config = Config(
            ("BookingSettings:MaxBroadcastMinutes", "10"),
            ("BookingSettings:ScheduledClaimLeadMinutes", "45"));
        var createdAt = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var scheduled = createdAt.AddHours(4);

        var deadline = BookingBroadcastQueueService.ComputeGiveUpDeadline(
            createdAt, scheduled, config, DeliveryMode.OnDemand);

        Assert.Equal(scheduled.AddMinutes(-45), deadline);
    }

    // --- Pulse cadence ---

    [Theory]
    [InlineData(5, 10)]      // fresh
    [InlineData(120, 30)]    // warm
    [InlineData(600, 60)]    // cool
    [InlineData(1800, 120)]  // stale
    public void Regular_pulse_cadence_is_unchanged_by_the_mode_lookup(int ageSeconds, int expectedSeconds)
    {
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var createdAt = now.AddSeconds(-ageSeconds);

        var next = BookingBroadcastQueueService.ComputeNextPulseAt(createdAt, now, Config());

        Assert.Equal(now.AddSeconds(expectedSeconds), next);
    }

    [Fact]
    public void A_mode_can_slow_its_own_pulse_cadence_without_touching_Regular()
    {
        var config = Config(("BookingSettings:DeliveryModes:Pooling:PulseIntervalWarmSeconds", "60"));
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
        var createdAt = now.AddMinutes(-2);  // warm tier

        Assert.Equal(now.AddSeconds(60),
            BookingBroadcastQueueService.ComputeNextPulseAt(createdAt, now, config, DeliveryMode.Pooling));
        Assert.Equal(now.AddSeconds(30),
            BookingBroadcastQueueService.ComputeNextPulseAt(createdAt, now, config));
    }
}
