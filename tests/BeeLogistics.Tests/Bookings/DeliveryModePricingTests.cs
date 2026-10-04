using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// These are the numbers that decide what a customer is charged for choosing a mode, so they are
/// tested exhaustively rather than through the fare service.
///
/// The behaviour worth guarding hardest is what happens when the config is wrong: a missing or
/// malformed section must be a no-op. A default premium would overcharge silently, and a default
/// discount would give deliveries away — either is worse than the mode simply doing nothing.
/// </summary>
public class DeliveryModePricingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    private static IConfiguration Standard() => Config(
        ("Pricing:DeliveryModes:OnDemand:Multiplier", "1.25"),
        ("Pricing:DeliveryModes:OnDemand:FlatFee", "0"),
        ("Pricing:DeliveryModes:OnDemand:MinFee", "20"),
        ("Pricing:DeliveryModes:OnDemand:MaxFee", "500"),
        ("Pricing:DeliveryModes:Pooling:DiscountRate", "0.15"),
        ("Pricing:DeliveryModes:Pooling:MaxDiscount", "150"));

    // --- Reading config ---

    [Fact]
    public void An_unconfigured_mode_reads_as_no_adjustment()
    {
        var rates = DeliveryModePricing.Read(Config(), DeliveryMode.OnDemand);

        Assert.Equal(ModePricing.None, rates);
        Assert.Equal(0m, DeliveryModePricing.Premium(1000m, rates));
        Assert.Equal(0m, DeliveryModePricing.PoolingDiscount(1000m, rates));
    }

    [Fact]
    public void A_present_but_empty_section_reads_as_no_adjustment()
    {
        // "Regular": {} is written in appsettings on purpose, to document that the mode exists and
        // costs nothing. It must not be mistaken for a configured premium.
        var rates = DeliveryModePricing.Read(
            Config(("Pricing:DeliveryModes:Regular:Multiplier", "1.0")), DeliveryMode.Regular);

        Assert.Equal(0m, DeliveryModePricing.Premium(1000m, rates));
    }

    [Fact]
    public void Partial_config_leaves_the_unspecified_knobs_neutral()
    {
        var rates = DeliveryModePricing.Read(
            Config(("Pricing:DeliveryModes:OnDemand:Multiplier", "1.5")), DeliveryMode.OnDemand);

        Assert.Equal(0m, rates.MaxFee);      // uncapped
        Assert.Equal(50m, DeliveryModePricing.Premium(100m, rates));
    }

    // --- On-Demand premium ---

    [Fact]
    public void The_premium_is_the_multiplier_applied_to_the_subtotal()
    {
        var rates = DeliveryModePricing.Read(Standard(), DeliveryMode.OnDemand);

        Assert.Equal(31.00m, DeliveryModePricing.Premium(124m, rates));   // 124 x 0.25
    }

    [Fact]
    public void The_premium_is_floored_by_MinFee()
    {
        // A very short trip should still pay something for jumping the queue.
        var rates = DeliveryModePricing.Read(Standard(), DeliveryMode.OnDemand);

        Assert.Equal(20.00m, DeliveryModePricing.Premium(40m, rates));    // 40 x 0.25 = 10 -> 20
    }

    [Fact]
    public void The_premium_is_capped_by_MaxFee()
    {
        var rates = DeliveryModePricing.Read(Standard(), DeliveryMode.OnDemand);

        Assert.Equal(500.00m, DeliveryModePricing.Premium(10_000m, rates));  // 2500 -> 500
    }

    [Fact]
    public void A_flat_fee_is_added_before_clamping()
    {
        var rates = DeliveryModePricing.Read(Config(
            ("Pricing:DeliveryModes:OnDemand:Multiplier", "1.1"),
            ("Pricing:DeliveryModes:OnDemand:FlatFee", "15")), DeliveryMode.OnDemand);

        Assert.Equal(25.00m, DeliveryModePricing.Premium(100m, rates));   // 10 + 15
    }

    [Fact]
    public void A_multiplier_of_one_yields_no_premium_even_with_a_MinFee()
    {
        // MinFee must not conjure a charge out of a mode that carries no premium — otherwise
        // clearing the multiplier to disable On-Demand would still bill every customer MinFee.
        var rates = DeliveryModePricing.Read(Config(
            ("Pricing:DeliveryModes:OnDemand:Multiplier", "1.0"),
            ("Pricing:DeliveryModes:OnDemand:MinFee", "20")), DeliveryMode.OnDemand);

        Assert.Equal(0m, DeliveryModePricing.Premium(500m, rates));
    }

    [Fact]
    public void The_premium_rounds_to_two_decimals()
    {
        var rates = DeliveryModePricing.Read(Config(
            ("Pricing:DeliveryModes:OnDemand:Multiplier", "1.25")), DeliveryMode.OnDemand);

        Assert.Equal(25.13m, DeliveryModePricing.Premium(100.51m, rates));  // 25.1275
    }

    // --- Pooling discount ---

    [Fact]
    public void The_discount_is_the_rate_applied_to_the_subtotal()
    {
        var rates = DeliveryModePricing.Read(Standard(), DeliveryMode.Pooling);

        Assert.Equal(18.60m, DeliveryModePricing.PoolingDiscount(124m, rates));  // 124 x 0.15
    }

    [Fact]
    public void The_discount_is_capped_by_MaxDiscount()
    {
        var rates = DeliveryModePricing.Read(Standard(), DeliveryMode.Pooling);

        Assert.Equal(150.00m, DeliveryModePricing.PoolingDiscount(10_000m, rates));
    }

    [Fact]
    public void The_discount_never_exceeds_the_subtotal()
    {
        // A misconfigured rate above 1.0 would otherwise produce a negative fare, which propagates
        // into the driver's earnings quote and the cash they collect.
        var rates = DeliveryModePricing.Read(Config(
            ("Pricing:DeliveryModes:Pooling:DiscountRate", "5.0")), DeliveryMode.Pooling);

        Assert.Equal(100m, DeliveryModePricing.PoolingDiscount(100m, rates));
    }

    [Fact]
    public void A_zero_rate_yields_no_discount()
    {
        var rates = DeliveryModePricing.Read(Config(
            ("Pricing:DeliveryModes:Pooling:DiscountRate", "0")), DeliveryMode.Pooling);

        Assert.Equal(0m, DeliveryModePricing.PoolingDiscount(500m, rates));
    }

    // --- Degenerate inputs ---

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void A_nonpositive_subtotal_yields_no_adjustment(decimal subtotal)
    {
        var onDemand = DeliveryModePricing.Read(Standard(), DeliveryMode.OnDemand);
        var pooling = DeliveryModePricing.Read(Standard(), DeliveryMode.Pooling);

        Assert.Equal(0m, DeliveryModePricing.Premium(subtotal, onDemand));
        Assert.Equal(0m, DeliveryModePricing.PoolingDiscount(subtotal, pooling));
    }

    [Fact]
    public void Malformed_config_does_not_throw()
    {
        // A bad deploy should price as though the mode carried no adjustment, not 500 the request.
        var config = Config(
            ("Pricing:DeliveryModes:OnDemand:Multiplier", "not-a-number"),
            ("Pricing:DeliveryModes:OnDemand:MaxFee", ""));

        var ex = Record.Exception(() =>
        {
            var rates = DeliveryModePricing.Read(config, DeliveryMode.OnDemand);
            DeliveryModePricing.Premium(100m, rates);
        });

        Assert.Null(ex);
    }
}
