using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

/// <summary>
/// Vehicle pricing configuration entity with versioning support.
/// When pricing is updated, a new version is created for audit/history tracking.
/// </summary>
public class VehiclePricing : Entity
{
    public string VehicleType { get; private set; } = null!;
    public string? Types { get; private set; } // e.g., "Hatchback/Sedan", "Subcompact SUV / Crossover"
    public bool IsActive { get; private set; } = true;
    
    // Standard pricing
    public decimal BaseFare { get; private set; }
    public decimal PerKm0to5 { get; private set; }
    public decimal PerKmAbove5 { get; private set; }
    public decimal AdditionalStopFee { get; private set; }
    
    // Weight and size limits
    public decimal? WeightLimitKg { get; private set; }
    public decimal WeightSurchargePerKg { get; private set; }
    public string? SizeLimit { get; private set; }
    
    // Long distance pricing (nullable - not all vehicle types have this)
    public decimal? LongDistanceBaseFare { get; private set; }
    public decimal? LongDistancePerKm41to60 { get; private set; }
    public decimal? LongDistancePerKmAbove60 { get; private set; }
    
    // Additional info
    public string? SurchargeInfo { get; private set; }
    public string? Remarks { get; private set; }
    
    // Version tracking
    public int Version { get; private set; } = 1;
    public Guid? UpdatedByUserId { get; private set; }
    public string? UpdatedByUserName { get; private set; }
    
    // Navigation property for version history
    private readonly List<VehiclePricingVersion> _versions = new();
    public IReadOnlyCollection<VehiclePricingVersion> Versions => _versions.AsReadOnly();
    
    // EF Core needs access to the backing field
    internal void AddVersion(VehiclePricingVersion version)
    {
        _versions.Add(version);
    }

    private VehiclePricing() { } // For EF Core

    public VehiclePricing(
        string vehicleType,
        decimal baseFare,
        decimal perKm0to5,
        decimal perKmAbove5,
        decimal additionalStopFee,
        decimal? weightLimitKg = null,
        decimal weightSurchargePerKg = 0,
        string? sizeLimit = null,
        string? types = null,
        decimal? longDistanceBaseFare = null,
        decimal? longDistancePerKm41to60 = null,
        decimal? longDistancePerKmAbove60 = null,
        string? surchargeInfo = null,
        string? remarks = null,
        Guid? createdByUserId = null,
        string? createdByUserName = null)
    {
        if (string.IsNullOrWhiteSpace(vehicleType))
            throw new ArgumentException("Vehicle type is required", nameof(vehicleType));
        
        if (baseFare < 0)
            throw new ArgumentException("Base fare cannot be negative", nameof(baseFare));
        
        if (perKm0to5 < 0)
            throw new ArgumentException("PerKm0to5 cannot be negative", nameof(perKm0to5));
        
        if (perKmAbove5 < 0)
            throw new ArgumentException("PerKmAbove5 cannot be negative", nameof(perKmAbove5));
        
        if (additionalStopFee < 0)
            throw new ArgumentException("Additional stop fee cannot be negative", nameof(additionalStopFee));

        Id = Guid.NewGuid();
        VehicleType = vehicleType;
        Types = types;
        BaseFare = baseFare;
        PerKm0to5 = perKm0to5;
        PerKmAbove5 = perKmAbove5;
        AdditionalStopFee = additionalStopFee;
        WeightLimitKg = weightLimitKg;
        WeightSurchargePerKg = weightSurchargePerKg;
        SizeLimit = sizeLimit;
        LongDistanceBaseFare = longDistanceBaseFare;
        LongDistancePerKm41to60 = longDistancePerKm41to60;
        LongDistancePerKmAbove60 = longDistancePerKmAbove60;
        SurchargeInfo = surchargeInfo;
        Remarks = remarks;
        IsActive = true;
        Version = 1;
        UpdatedByUserId = createdByUserId;
        UpdatedByUserName = createdByUserName;
        
        // Initial version will be created by repository/service layer
        // to ensure proper EF Core tracking
    }

    /// <summary>
    /// Updates pricing and creates a new version for audit tracking.
    /// </summary>
    public void UpdatePricing(
        decimal baseFare,
        decimal perKm0to5,
        decimal perKmAbove5,
        decimal additionalStopFee,
        decimal? weightLimitKg = null,
        decimal weightSurchargePerKg = 0,
        string? sizeLimit = null,
        string? types = null,
        decimal? longDistanceBaseFare = null,
        decimal? longDistancePerKm41to60 = null,
        decimal? longDistancePerKmAbove60 = null,
        string? surchargeInfo = null,
        string? remarks = null,
        Guid? updatedByUserId = null,
        string? updatedByUserName = null)
    {
        if (baseFare < 0)
            throw new ArgumentException("Base fare cannot be negative", nameof(baseFare));
        
        if (perKm0to5 < 0)
            throw new ArgumentException("PerKm0to5 cannot be negative", nameof(perKm0to5));
        
        if (perKmAbove5 < 0)
            throw new ArgumentException("PerKmAbove5 cannot be negative", nameof(perKmAbove5));
        
        if (additionalStopFee < 0)
            throw new ArgumentException("Additional stop fee cannot be negative", nameof(additionalStopFee));

        // Version snapshot will be created by repository/service layer
        // to ensure proper EF Core tracking

        // Update current pricing
        BaseFare = baseFare;
        PerKm0to5 = perKm0to5;
        PerKmAbove5 = perKmAbove5;
        AdditionalStopFee = additionalStopFee;
        WeightLimitKg = weightLimitKg;
        WeightSurchargePerKg = weightSurchargePerKg;
        SizeLimit = sizeLimit;
        Types = types;
        LongDistanceBaseFare = longDistanceBaseFare;
        LongDistancePerKm41to60 = longDistancePerKm41to60;
        LongDistancePerKmAbove60 = longDistancePerKmAbove60;
        SurchargeInfo = surchargeInfo;
        Remarks = remarks;
        Version++;
        UpdatedByUserId = updatedByUserId;
        UpdatedByUserName = updatedByUserName;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }
}
