using BeeLogistics.Modules.Bookings.Application.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The fare a customer is charged is now the server's number, not the client's. These tests pin
/// the asymmetry in that rule, which is the part most likely to be "simplified" into a plain
/// equality check by someone who has not hit the failure modes.
///
/// Rejecting every mismatch would 400 honest customers: the high-demand surcharge reads UtcNow,
/// so tapping Confirm at 06:59:59 crosses into a peak window between quote and submit. Rejecting
/// nothing would let a client be charged materially more than the price they consented to.
/// </summary>
public class FareTrustTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    [Fact]
    public void Tolerance_takes_the_larger_of_the_flat_floor_and_the_percentage()
    {
        var config = Config(
            ("Pricing:FareTrust:ToleranceAbsolute", "25.00"),
            ("Pricing:FareTrust:ToleranceRate", "0.10"));

        // A cheap booking is governed by the flat floor: 10% of 100 is only 10.
        Assert.Equal(25.00m, FareTrust.Tolerance(100m, config));

        // An expensive one by the percentage, so long trips get proportional headroom.
        Assert.Equal(90.00m, FareTrust.Tolerance(900m, config));
    }

    [Fact]
    public void Tolerance_falls_back_to_defaults_when_unconfigured()
    {
        var tolerance = FareTrust.Tolerance(100m, Config());
        Assert.Equal(FareTrust.DefaultToleranceAbsolute, tolerance);
    }

    [Fact]
    public void Enforcement_is_off_unless_explicitly_switched_on()
    {
        // Ships off deliberately: the server fare is used from day one, but the customer-visible
        // 400 waits until the drift histogram says it is safe.
        Assert.False(FareTrust.IsEnforced(Config()));
        Assert.True(FareTrust.IsEnforced(Config(("Pricing:FareTrust:Enforce", "true"))));
    }

    [Theory]
    // quoted, server, description
    [InlineData(300, 300)]   // exact agreement
    [InlineData(300, 310)]   // server dearer, but inside ₱25
    [InlineData(300, 325)]   // server dearer by exactly the tolerance — boundary is inclusive
    [InlineData(300, 250)]   // server cheaper: charge less, never reject
    [InlineData(300, 1)]     // server far cheaper
    [InlineData(0, 300)]     // no quote presented at all
    [InlineData(-5, 300)]    // nonsense quote treated as "none"
    public void Proceeds_on_the_server_fare(decimal quoted, decimal server)
    {
        var decision = FareTrust.Decide(quoted, server, tolerance: 25m, enforce: true);
        Assert.Equal(FareTrustDecision.UseServerFare, decision);
    }

    [Fact]
    public void Requires_a_requote_when_the_server_is_materially_dearer()
    {
        // The manipulation case — POST estimatedFare: 1 against a real ₱312 fare — and the
        // stale-quote case are the same shape, and both need the customer to see the new price.
        var decision = FareTrust.Decide(quotedFare: 1m, serverFare: 312m, tolerance: 25m, enforce: true);
        Assert.Equal(FareTrustDecision.RequoteRequired, decision);
    }

    [Fact]
    public void A_client_claiming_a_higher_fare_is_never_rejected()
    {
        // Charging someone less than they agreed to needs no consent. Rejecting here would punish
        // a customer for our own price drop.
        var decision = FareTrust.Decide(quotedFare: 5000m, serverFare: 300m, tolerance: 25m, enforce: true);
        Assert.Equal(FareTrustDecision.UseServerFare, decision);
    }

    [Theory]
    [InlineData(1, 312)]
    [InlineData(300, 900)]
    [InlineData(0, 500)]
    public void Nothing_is_rejected_while_enforcement_is_off(decimal quoted, decimal server)
    {
        // The security fix still applies — callers persist the server fare regardless. Only the
        // customer-visible refusal is gated.
        var decision = FareTrust.Decide(quoted, server, tolerance: 25m, enforce: false);
        Assert.Equal(FareTrustDecision.UseServerFare, decision);
    }
}
