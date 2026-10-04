using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The first tests over PricingService, which was entirely uncovered while being the thing that
/// decides what a customer pays and what a driver earns.
///
/// Their main job is to be a regression net: the golden-value cases below pin today's arithmetic
/// exactly, so the per-mode pricing work in #67 changes fares only where it means to.
/// </summary>
public class PricingServiceTests
{
    private readonly IPricingConfigurationService _config = Substitute.For<IPricingConfigurationService>();
    private readonly IRouteDistanceCalculationService _distance = Substitute.For<IRouteDistanceCalculationService>();
    private readonly IHighDemandSurchargeService _surcharge = Substitute.For<IHighDemandSurchargeService>();
    private readonly Dictionary<string, string?> _settings = new();

    /// <summary>
    /// Rebuilt per access: AddInMemoryCollection snapshots the dictionary at Build(), so a service
    /// constructed in the ctor would never see rates a test adds afterwards.
    /// </summary>
    private PricingService Service => new(
        _config, _distance, _surcharge,
        new ConfigurationBuilder().AddInMemoryCollection(_settings).Build(),
        NullLogger<PricingService>.Instance);

    public PricingServiceTests()
    {
        // No surge unless a test asks for one; 1.0 is the "nothing applies" contract.
        _surcharge.GetHighDemandMultiplierAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(1.0m);
    }

    private void WithVehicle(decimal baseFare = 50m, decimal perKm0to5 = 10m, decimal perKmAbove5 = 8m,
        decimal? weightLimitKg = null, decimal weightSurchargePerKg = 0m)
        => _config.GetVehiclePricingConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VehiclePricingConfig
            {
                BaseFare = baseFare,
                PerKm0to5 = perKm0to5,
                PerKmAbove5 = perKmAbove5,
                AdditionalStopFee = 30m,
                WeightLimitKg = weightLimitKg,
                WeightSurchargePerKg = weightSurchargePerKg,
            });

    private void WithDistance(decimal km)
        => _distance.CalculateRouteDistanceAsync(Arg.Any<List<DeliveryStop>>(), Arg.Any<CancellationToken>())
            .Returns(km);

    private static List<DeliveryStop> TwoStops() =>
    [
        new(Guid.Empty, 0, "Pickup St", StopType.Pickup, 16.61m, 120.31m),
        new(Guid.Empty, 1, "Dropoff Ave", StopType.Dropoff, 16.62m, 120.33m),
    ];

    [Fact]
    public async Task Golden_value_for_a_short_trip()
    {
        WithVehicle();
        WithDistance(3m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));

        Assert.Equal(50m, result.BaseFare);
        Assert.Equal(30m, result.DistanceFare);   // 3 km entirely in the 0-5 band
        Assert.Equal(80m, result.TotalFare);
    }

    [Fact]
    public async Task Golden_value_for_a_trip_past_the_distance_band()
    {
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));

        Assert.Equal(74m, result.DistanceFare);   // 5x10 + 3x8
        Assert.Equal(124m, result.TotalFare);
    }

    [Fact]
    public async Task Exactly_five_km_stays_in_the_cheaper_band()
    {
        // The boundary is `distanceKm <= 5`, so 5.0 must not be split across both rates.
        WithVehicle();
        WithDistance(5m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));

        Assert.Equal(50m, result.DistanceFare);
    }

    [Fact]
    public async Task A_single_dropoff_is_no_longer_charged_a_multi_stop_fee()
    {
        // This used to be `dropoffCount * AdditionalStopFee`, so every ordinary booking paid one
        // "additional" stop fee for its only dropoff. Removing it was a fare correction, and this
        // test exists so it cannot quietly come back.
        WithVehicle();
        WithDistance(3m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));

        Assert.Equal(80m, result.TotalFare);      // not 110
        Assert.DoesNotContain("stop", result.Breakdown!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Weight_surcharge_applies_only_above_the_limit()
    {
        WithVehicle(weightLimitKg: 20m, weightSurchargePerKg: 5m);
        WithDistance(3m);

        var under = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops(), WeightKg: 20m));
        Assert.Equal(0m, under.WeightSurcharge);

        var over = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops(), WeightKg: 23m));
        Assert.Equal(15m, over.WeightSurcharge);  // 3 kg over x 5
    }

    [Fact]
    public async Task No_weight_surcharge_when_the_vehicle_has_no_limit()
    {
        WithVehicle(weightLimitKg: null, weightSurchargePerKg: 5m);
        WithDistance(3m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops(), WeightKg: 500m));

        Assert.Equal(0m, result.WeightSurcharge);
    }

    [Fact]
    public async Task High_demand_is_a_delta_on_the_subtotal_not_a_multiplier_on_the_total()
    {
        // Applied as baseTotal * (multiplier - 1) so it stacks additively with the premium rather
        // than multiplying it. #67 adds a priority premium that must NOT be surged.
        WithVehicle();
        WithDistance(3m);
        _surcharge.GetHighDemandMultiplierAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(1.5m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));

        Assert.Equal(40m, result.HighDemandSurcharge);   // (50 + 30) * 0.5
        Assert.Equal(120m, result.TotalFare);
    }

    [Fact]
    public async Task Unknown_vehicle_type_falls_back_to_default_pricing()
    {
        _config.GetVehiclePricingConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VehiclePricingConfig?)null);
        WithDistance(4m);

        var result = await Service.CalculateFareAsync(new FareRequest("Hovercraft", TwoStops()));

        Assert.Equal(50m, result.BaseFare);      // hardcoded default
        Assert.Equal(40m, result.DistanceFare);  // 4 km x ₱10
        Assert.Equal(90m, result.TotalFare);
    }

    [Fact]
    public async Task Total_is_the_sum_of_its_parts()
    {
        WithVehicle(weightLimitKg: 10m, weightSurchargePerKg: 2m);
        WithDistance(12m);
        _surcharge.GetHighDemandMultiplierAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(1.2m);

        var r = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops(), WeightKg: 15m));

        var parts = r.BaseFare + r.DistanceFare + r.WeightSurcharge
                  + r.PriorityFee + r.HighDemandSurcharge + r.TollFee;
        Assert.Equal(parts, r.TotalFare);
    }

    [Fact]
    public async Task Breakdown_omits_zero_lines_and_ends_with_the_total()
    {
        WithVehicle();
        WithDistance(3m);

        var result = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));
        var lines = result.Breakdown!.Split('\n');

        Assert.DoesNotContain(lines, l => l.Contains("Weight", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Toll", StringComparison.Ordinal));
        Assert.StartsWith("Total:", lines[^1], StringComparison.Ordinal);
    }

    // --- Per-mode pricing ---

    /// <summary>
    /// The shipped rates: the rate card is the Pooling price, and the faster tiers mark it up.
    /// Pooling carries no entry at all, which is the point — nothing is discounted, so no mode
    /// prices below the card and no mode pays the driver less than another job of the same distance.
    /// </summary>
    private void WithModeRates()
    {
        _settings["Pricing:DeliveryModes:Regular:Multiplier"] = "1.05";
        _settings["Pricing:DeliveryModes:OnDemand:Multiplier"] = "1.25";
        _settings["Pricing:DeliveryModes:OnDemand:MinFee"] = "20";
        _settings["Pricing:DeliveryModes:OnDemand:MaxFee"] = "500";
    }

    [Fact]
    public async Task Pooling_is_the_rate_card_itself()
    {
        // ₱124 is the golden subtotal from the tests above — the unmodified card. It is now the
        // Pooling price, where it used to be the Regular price.
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Equal(124m, result.TotalFare);
        Assert.Equal(0m, result.PriorityFee);
        Assert.Equal(0m, result.PoolingDiscount);
    }

    [Fact]
    public async Task Regular_carries_its_configured_markup_over_the_card()
    {
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Regular));

        Assert.Equal(6.20m, result.PriorityFee);    // 124 x 0.05
        Assert.Equal(130.20m, result.TotalFare);
    }

    [Fact]
    public async Task No_mode_prices_below_the_rate_card()
    {
        // The property that removes the pooled pay cut: the driver's share is a fraction of the
        // gross, so as long as no mode dips under the card, no mode underpays relative to another.
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        foreach (var mode in Enum.GetValues<DeliveryMode>())
        {
            var result = await Service.CalculateFareAsync(
                new FareRequest("Motorcycle", TwoStops(), DeliveryMode: mode));

            Assert.True(result.TotalFare >= 124m, $"{mode} priced at {result.TotalFare}, below the card");
        }
    }

    [Fact]
    public async Task OnDemand_adds_the_premium()
    {
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));

        Assert.Equal(31.00m, result.PriorityFee);     // 124 x 0.25
        Assert.Equal(0m, result.PoolingDiscount);
        Assert.Equal(155.00m, result.TotalFare);
    }

    /// <summary>
    /// The discount mechanism is retained but no longer used by the shipped rates — Pooling is the
    /// card, so it has nothing to be discounted from. Kept tested because the code path is still
    /// there for the day a mode genuinely has to price below the card, and an untested path that
    /// touches money rots.
    /// </summary>
    [Fact]
    public async Task A_mode_can_still_be_configured_below_the_card_if_it_ever_has_to_be()
    {
        _settings["Pricing:DeliveryModes:Pooling:DiscountRate"] = "0.15";
        _settings["Pricing:DeliveryModes:Pooling:MaxDiscount"] = "150";
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Equal(18.60m, result.PoolingDiscount);  // 124 x 0.15
        Assert.Equal(0m, result.PriorityFee);
        Assert.Equal(105.40m, result.TotalFare);
    }

    /// <summary>
    /// Replaces <c>A_surge_does_not_multiply_the_premium</c>, which asserted the opposite and was
    /// deliberately inverted.
    ///
    /// That rule surged the pre-markup subtotal, so the premium survived a surge in pesos but
    /// shrank in proportion: at 2x, a +25% On-Demand booking was only +12.5% over Regular. The
    /// tiers converged exactly when speed was worth the most to the customer and the trip cost the
    /// most to serve — so the old test was pinning a bug, not a decision.
    /// </summary>
    [Theory]
    [InlineData(1.0, 130.20, 155.00)]   // no surge
    [InlineData(2.0, 260.40, 310.00)]   // 2x
    [InlineData(3.0, 390.60, 465.00)]   // 3x
    public async Task The_mode_spacing_survives_any_surge(
        decimal surge, decimal expectedRegular, decimal expectedOnDemand)
    {
        WithModeRates();
        WithVehicle();
        WithDistance(8m);
        _surcharge.GetHighDemandMultiplierAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(surge);

        var regular = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Regular));
        var onDemand = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));

        Assert.Equal(expectedRegular, regular.TotalFare);
        Assert.Equal(expectedOnDemand, onDemand.TotalFare);

        // The invariant, stated independently of the numbers above: On-Demand stays 1.25/1.05
        // dearer than Regular at every multiplier.
        Assert.Equal(
            Math.Round(1.25m / 1.05m, 4),
            Math.Round(onDemand.TotalFare / regular.TotalFare, 4));
    }

    [Fact]
    public async Task A_surge_still_multiplies_only_the_delivery_and_not_the_card_twice()
    {
        // Sanity that the surcharge is still a delta rather than becoming a second multiplication:
        // On-Demand at 8 km is a ₱155 mode subtotal, so a 2x surge adds ₱155, not ₱310.
        WithModeRates();
        WithVehicle();
        WithDistance(8m);
        _surcharge.GetHighDemandMultiplierAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(2.0m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));

        Assert.Equal(31.00m, result.PriorityFee);         // 124 x 0.25, unchanged by the surge
        Assert.Equal(155.00m, result.HighDemandSurcharge); // (124 + 31) x 1.0
        Assert.Equal(310.00m, result.TotalFare);
    }

    [Fact]
    public async Task The_mode_is_echoed_on_the_result()
    {
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Equal(DeliveryMode.Pooling, result.DeliveryMode);
    }

    [Fact]
    public async Task Mode_pricing_applies_on_the_unknown_vehicle_fallback_too()
    {
        // Otherwise an unrecognised vehicle type is a way to get On-Demand for free.
        WithModeRates();
        _config.GetVehiclePricingConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((VehiclePricingConfig?)null);
        WithDistance(4m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Hovercraft", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));

        Assert.Equal(22.50m, result.PriorityFee);   // (50 + 40) x 0.25
        Assert.Equal(112.50m, result.TotalFare);
    }

    [Fact]
    public async Task An_absurd_discount_cannot_produce_a_negative_fare()
    {
        _settings["Pricing:DeliveryModes:Pooling:DiscountRate"] = "10.0";
        WithVehicle();
        WithDistance(3m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Equal(0m, result.TotalFare);
        Assert.True(result.TotalFare >= 0m);
    }

    [Fact]
    public async Task The_breakdown_shows_a_discount_as_a_negative_line()
    {
        _settings["Pricing:DeliveryModes:Pooling:DiscountRate"] = "0.15";
        WithVehicle();
        WithDistance(8m);

        var result = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));
        var lines = result.Breakdown!.Split('\n');

        Assert.Contains(lines, l => l.Contains("Pooling Discount: -", StringComparison.Ordinal));
        Assert.StartsWith("Total:", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_breakdown_names_the_mode_that_earned_the_markup()
    {
        // A fixed "Priority Fee" label would now appear on a Regular booking, which reads as an
        // error to the customer paying it.
        WithModeRates();
        WithVehicle();
        WithDistance(8m);

        var regular = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Regular));
        var onDemand = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));
        var pooling = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Contains("Regular Service: ₱6.20", regular.Breakdown!, StringComparison.Ordinal);
        Assert.Contains("On-Demand Premium: ₱31.00", onDemand.Breakdown!, StringComparison.Ordinal);
        // Pooling is the card, so there is no markup line at all rather than a ₱0.00 one.
        Assert.DoesNotContain("Premium", pooling.Breakdown!, StringComparison.Ordinal);
        Assert.DoesNotContain("Service:", pooling.Breakdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_mode_config_every_mode_prices_the_same()
    {
        // Ships inert: deploying the code without the rates must not move any price.
        WithVehicle();
        WithDistance(8m);

        var regular = await Service.CalculateFareAsync(new FareRequest("Motorcycle", TwoStops()));
        var onDemand = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.OnDemand));
        var pooling = await Service.CalculateFareAsync(
            new FareRequest("Motorcycle", TwoStops(), DeliveryMode: DeliveryMode.Pooling));

        Assert.Equal(regular.TotalFare, onDemand.TotalFare);
        Assert.Equal(regular.TotalFare, pooling.TotalFare);
    }

    [Fact]
    public async Task No_stops_is_rejected_rather_than_priced_as_zero()
    {
        WithVehicle();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service.CalculateFareAsync(new FareRequest("Motorcycle", [])));
    }
}
