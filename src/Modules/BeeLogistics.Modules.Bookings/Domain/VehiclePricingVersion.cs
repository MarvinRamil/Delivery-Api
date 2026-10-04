using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

/// <summary>
/// Historical version of vehicle pricing for audit and tracking purposes.
/// A new version is created whenever pricing is updated.
/// </summary>
public class VehiclePricingVersion : Entity
{
    public Guid VehiclePricingId { get; private set; }
    public int Version { get; private set; }
    
    // Pricing snapshot
    public decimal BaseFare { get; private set; }
    public decimal PerKm0to5 { get; private set; }
    public decimal PerKmAbove5 { get; private set; }
    public decimal AdditionalStopFee { get; private set; }
    public decimal? WeightLimitKg { get; private set; }
    public decimal WeightSurchargePerKg { get; private set; }
    public string? SizeLimit { get; private set; }
    public string? Types { get; private set; }
    public decimal? LongDistanceBaseFare { get; private set; }
    public decimal? LongDistancePerKm41to60 { get; private set; }
    public decimal? LongDistancePerKmAbove60 { get; private set; }
    public string? SurchargeInfo { get; private set; }
    public string? Remarks { get; private set; }
    
    // Who made the change
    public Guid? ChangedByUserId { get; private set; }
    public string? ChangedByUserName { get; private set; }
    
    // Navigation property
    public VehiclePricing VehiclePricing { get; private set; } = null!;

    private VehiclePricingVersion() { } // For EF Core

    public VehiclePricingVersion(
        Guid vehiclePricingId,
        int version,
        decimal baseFare,
        decimal perKm0to5,
        decimal perKmAbove5,
        decimal additionalStopFee,
        decimal? weightLimitKg,
        decimal weightSurchargePerKg,
        string? sizeLimit,
        string? types,
        decimal? longDistanceBaseFare,
        decimal? longDistancePerKm41to60,
        decimal? longDistancePerKmAbove60,
        string? surchargeInfo,
        string? remarks,
        Guid? changedByUserId = null,
        string? changedByUserName = null)
    {
        Id = Guid.NewGuid();
        VehiclePricingId = vehiclePricingId;
        Version = version;
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
        ChangedByUserId = changedByUserId;
        ChangedByUserName = changedByUserName;
    }
}
