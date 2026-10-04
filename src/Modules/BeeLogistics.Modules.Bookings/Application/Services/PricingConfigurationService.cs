using BeeLogistics.Modules.Bookings.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Implementation that reads pricing configuration from database.
/// Follows Single Responsibility Principle (SRP) - only handles configuration reading.
/// </summary>
public class PricingConfigurationService : IPricingConfigurationService
{
    private readonly IVehiclePricingRepository _pricingRepository;
    private readonly IConfiguration _configuration;

    public PricingConfigurationService(
        IVehiclePricingRepository pricingRepository,
        IConfiguration configuration)
    {
        _pricingRepository = pricingRepository;
        _configuration = configuration;
    }

    public async Task<VehiclePricingConfig?> GetVehiclePricingConfigAsync(string vehicleType, CancellationToken ct = default)
    {
        // Read from database first
        var pricing = await _pricingRepository.GetByVehicleTypeAsync(vehicleType, ct);
        
        if (pricing != null)
        {
            return new VehiclePricingConfig
            {
                BaseFare = pricing.BaseFare,
                PerKm0to5 = pricing.PerKm0to5,
                PerKmAbove5 = pricing.PerKmAbove5,
                AdditionalStopFee = pricing.AdditionalStopFee,
                WeightLimitKg = pricing.WeightLimitKg,
                WeightSurchargePerKg = pricing.WeightSurchargePerKg,
                SizeLimit = pricing.SizeLimit
            };
        }

        // If not found in DB, return null (no fallback to config)
        return null;
    }

    public Task<HighDemandConfig> GetHighDemandConfigAsync(CancellationToken ct = default)
    {
        // High demand config still comes from appsettings.json
        var highDemandConfig = _configuration.GetSection("Pricing:HighDemandSurcharge");
        var config = new HighDemandConfig
        {
            MaxMultiplier = highDemandConfig.GetValue<decimal>("MaxMultiplier", 1.0m),
            PeakHours = highDemandConfig.GetSection("PeakHours").Get<string[]>() ?? Array.Empty<string>()
        };

        return Task.FromResult(config);
    }
}
