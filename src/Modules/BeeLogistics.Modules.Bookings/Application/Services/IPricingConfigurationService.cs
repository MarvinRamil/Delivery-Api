namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Service for reading pricing configuration.
/// Follows Interface Segregation Principle (ISP) - single responsibility.
/// Follows Dependency Inversion Principle (DIP) - depends on abstraction.
/// </summary>
public interface IPricingConfigurationService
{
    Task<VehiclePricingConfig?> GetVehiclePricingConfigAsync(string vehicleType, CancellationToken ct = default);
    Task<HighDemandConfig> GetHighDemandConfigAsync(CancellationToken ct = default);
}

public class VehiclePricingConfig
{
    public decimal BaseFare { get; set; }
    public decimal PerKm0to5 { get; set; }
    public decimal PerKmAbove5 { get; set; }
    public decimal AdditionalStopFee { get; set; }
    public decimal? WeightLimitKg { get; set; }
    public decimal WeightSurchargePerKg { get; set; }
    public string? SizeLimit { get; set; }
}

public class HighDemandConfig
{
    public decimal MaxMultiplier { get; set; }
    public string[] PeakHours { get; set; } = Array.Empty<string>();
}
