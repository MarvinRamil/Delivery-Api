using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Pricing service that calculates delivery fares.
/// Follows Single Responsibility Principle (SRP) - only handles fare calculation.
/// Follows Dependency Inversion Principle (DIP) - depends on abstractions.
/// </summary>
public class PricingService : IPricingService
{
    private readonly IPricingConfigurationService _configService;
    private readonly IRouteDistanceCalculationService _routeDistanceService;
    private readonly IHighDemandSurchargeService _surchargeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PricingService> _logger;

    public PricingService(
        IPricingConfigurationService configService,
        IRouteDistanceCalculationService routeDistanceService,
        IHighDemandSurchargeService surchargeService,
        IConfiguration configuration,
        ILogger<PricingService> logger)
    {
        _configService = configService;
        _routeDistanceService = routeDistanceService;
        _surchargeService = surchargeService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PricingResult> CalculateFareAsync(FareRequest request, CancellationToken ct = default)
    {
        var stops = request.Stops;
        var weightKg = request.WeightKg;
        var scheduledDateTime = request.ScheduledDateTime;

        if (stops == null || stops.Count == 0)
            throw new ArgumentException("At least one stop is required", nameof(request));

        var vehicleConfig = await _configService.GetVehiclePricingConfigAsync(request.VehicleType, ct);

        if (vehicleConfig == null)
        {
            _logger.LogWarning("Vehicle type {VehicleType} not found in configuration, using default pricing", request.VehicleType);
            return await CalculateDefaultFareAsync(request, ct);
        }

        // Calculate distance using injected service (DIP)
        var distanceKm = await _routeDistanceService.CalculateRouteDistanceAsync(stops, ct);
        
        // Base fare
        var result = new PricingResult
        {
            BaseFare = vehicleConfig.BaseFare,
            DistanceKm = distanceKm,
            DeliveryMode = request.DeliveryMode
        };

        // Distance-based fare
        if (distanceKm <= 5)
        {
            result.DistanceFare = distanceKm * vehicleConfig.PerKm0to5;
        }
        else
        {
            result.DistanceFare = (5 * vehicleConfig.PerKm0to5) + ((distanceKm - 5) * vehicleConfig.PerKmAbove5);
        }

        // No multi-stop fee: a booking is exactly one pickup and one dropoff (see Booking's ctor).
        // The old formula was `dropoffCount * AdditionalStopFee`, which charged every booking one
        // "additional" stop fee for its only dropoff — so removing it is a fare correction, not
        // just dead-code removal. VehiclePricing.AdditionalStopFee is left in place but unread.

        // Weight surcharge
        if (weightKg.HasValue && vehicleConfig.WeightLimitKg.HasValue && weightKg.Value > vehicleConfig.WeightLimitKg.Value)
        {
            var excessWeight = weightKg.Value - vehicleConfig.WeightLimitKg.Value;
            result.WeightSurcharge = excessWeight * vehicleConfig.WeightSurchargePerKg;
        }

        // The rate card IS the Pooling price. Every mode reads its own markup, so Pooling resolves
        // to 0 and the faster tiers add theirs — rather than Regular being the base and Pooling a
        // discount off it. Same shape as Lalamove, whose card is a single per-vehicle table with the
        // tier applied on top, and it is what keeps a pooled job from paying the driver less than
        // any other job: nothing is discounted, so nothing is below the card.
        var subtotal = result.BaseFare + result.DistanceFare + result.WeightSurcharge;
        var modeRates = DeliveryModePricing.Read(_configuration, request.DeliveryMode);
        result.PriorityFee = DeliveryModePricing.Premium(subtotal, modeRates);
        result.PoolingDiscount = DeliveryModePricing.PoolingDiscount(subtotal, modeRates);

        // The fare for this mode, before demand. This is what the surge multiplies.
        var modeSubtotal = subtotal + result.PriorityFee - result.PoolingDiscount;

        // High demand surcharge (using injected service - DIP)
        var highDemandMultiplier = await _surchargeService.GetHighDemandMultiplierAsync(scheduledDateTime ?? DateTime.UtcNow, ct);
        if (highDemandMultiplier > 1.0m)
        {
            // On the mode-adjusted subtotal, deliberately. This inverts the earlier rule, which
            // surged the pre-markup subtotal so a surge "would not also triple the On-Demand fee".
            // That preserved the premium in pesos but halved it in proportion: at a 2x surge a +25%
            // On-Demand booking was only +12.5% dearer than Regular, so the tiers converged exactly
            // when speed was worth the most to the customer and the trip cost the most to serve.
            // Surging the mode-adjusted amount holds the spacing at any multiplier. Pinned by test.
            result.HighDemandSurcharge = modeSubtotal * (highDemandMultiplier - 1.0m);
        }

        // Toll fee (would need to be calculated based on route)
        result.TollFee = 0; // TODO: Integrate with toll calculation service

        // Total fare
        // Floored at zero: no combination of discount and configuration may produce a negative
        // fare, which would propagate into the driver's earnings and the cash they collect.
        result.TotalFare = Math.Max(0m,
            modeSubtotal + result.HighDemandSurcharge + result.TollFee);

        // Generate breakdown string
        result.Breakdown = GenerateBreakdown(result);

        return result;
    }

    /// <summary>
    /// Fallback for a vehicle type with no pricing row. Mode adjustment applies here too — this
    /// path is reachable in production, so skipping it would make an unknown vehicle type a way to
    /// get On-Demand for free.
    /// </summary>
    private async Task<PricingResult> CalculateDefaultFareAsync(FareRequest request, CancellationToken ct)
    {
        var distanceKm = await _routeDistanceService.CalculateRouteDistanceAsync(request.Stops, ct);

        // Default pricing: ₱50 base + ₱10/km
        var baseFare = 50m;
        var perKm = 10m;
        var distanceFare = distanceKm * perKm;
        var subtotal = baseFare + distanceFare;

        // Same base-plus-markup rule as the primary path: the rate card is the Pooling price and
        // every mode reads its own markup. Not conditioned on the mode, because an unrecognised
        // vehicle type must not be a way to get the faster tiers for free.
        var modeRates = DeliveryModePricing.Read(_configuration, request.DeliveryMode);
        var premium = DeliveryModePricing.Premium(subtotal, modeRates);
        var poolingDiscount = DeliveryModePricing.PoolingDiscount(subtotal, modeRates);

        var result = new PricingResult
        {
            BaseFare = baseFare,
            DistanceFare = distanceFare,
            PriorityFee = premium,
            PoolingDiscount = poolingDiscount,
            DeliveryMode = request.DeliveryMode,
            DistanceKm = distanceKm,
            TotalFare = Math.Max(0m, subtotal + premium - poolingDiscount)
        };

        result.Breakdown = GenerateBreakdown(result);
        return result;
    }

    private string GenerateBreakdown(PricingResult result)
    {
        var breakdown = new List<string>
        {
            $"Base Fare: ₱{result.BaseFare:F2}",
            $"Distance ({result.DistanceKm:F2} km): ₱{result.DistanceFare:F2}"
        };

        if (result.WeightSurcharge > 0)
            breakdown.Add($"Weight Surcharge: ₱{result.WeightSurcharge:F2}");

        // Named after the mode that earned it. Every mode can now carry a markup, so a fixed
        // "Priority Fee" label would appear on a Regular booking and read as an error to the
        // customer. (The DTO member is still called PriorityFee — renaming a persisted column and a
        // wire field is a separate change, so the customer-visible string is fixed here first.)
        if (result.PriorityFee > 0)
            breakdown.Add($"{PremiumLabel(result.DeliveryMode)}: ₱{result.PriorityFee:F2}");

        if (result.HighDemandSurcharge > 0)
            breakdown.Add($"High Demand Surcharge: ₱{result.HighDemandSurcharge:F2}");

        // Explicit minus: this line reduces the total, and a customer scanning the breakdown
        // should not have to infer that from the label.
        if (result.PoolingDiscount > 0)
            breakdown.Add($"Pooling Discount: -₱{result.PoolingDiscount:F2}");

        if (result.TollFee > 0)
            breakdown.Add($"Toll Fee: ₱{result.TollFee:F2}");

        breakdown.Add($"Total: ₱{result.TotalFare:F2}");

        return string.Join("\n", breakdown);
    }

    /// <summary>
    /// What to call a mode's markup on the customer's breakdown.
    /// </summary>
    /// <remarks>
    /// Pooling has no case because it is the rate card — its markup is always 0, so the line is
    /// omitted entirely rather than shown as "Pooling: ₱0.00".
    /// </remarks>
    private static string PremiumLabel(DeliveryMode mode) => mode switch
    {
        DeliveryMode.OnDemand => "On-Demand Premium",
        DeliveryMode.Regular => "Regular Service",
        _ => "Service Premium",
    };
}
