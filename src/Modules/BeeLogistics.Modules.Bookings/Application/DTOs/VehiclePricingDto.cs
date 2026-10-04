namespace BeeLogistics.Modules.Bookings.Application.DTOs;

public record VehiclePricingDto(
    Guid Id,
    string VehicleType,
    string? Types,
    bool IsActive,
    decimal BaseFare,
    decimal PerKm0to5,
    decimal PerKmAbove5,
    decimal AdditionalStopFee,
    decimal? WeightLimitKg,
    decimal WeightSurchargePerKg,
    string? SizeLimit,
    decimal? LongDistanceBaseFare,
    decimal? LongDistancePerKm41to60,
    decimal? LongDistancePerKmAbove60,
    string? SurchargeInfo,
    string? Remarks,
    int Version,
    Guid? UpdatedByUserId,
    string? UpdatedByUserName,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateVehiclePricingDto(
    string VehicleType,
    string? Types,
    decimal BaseFare,
    decimal PerKm0to5,
    decimal PerKmAbove5,
    decimal AdditionalStopFee,
    decimal? WeightLimitKg,
    decimal WeightSurchargePerKg,
    string? SizeLimit,
    decimal? LongDistanceBaseFare,
    decimal? LongDistancePerKm41to60,
    decimal? LongDistancePerKmAbove60,
    string? SurchargeInfo,
    string? Remarks
);

public record UpdateVehiclePricingDto(
    string? Types,
    decimal BaseFare,
    decimal PerKm0to5,
    decimal PerKmAbove5,
    decimal AdditionalStopFee,
    decimal? WeightLimitKg,
    decimal WeightSurchargePerKg,
    string? SizeLimit,
    decimal? LongDistanceBaseFare,
    decimal? LongDistancePerKm41to60,
    decimal? LongDistancePerKmAbove60,
    string? SurchargeInfo,
    string? Remarks
);

public record VehiclePricingVersionDto(
    Guid Id,
    Guid VehiclePricingId,
    int Version,
    decimal BaseFare,
    decimal PerKm0to5,
    decimal PerKmAbove5,
    decimal AdditionalStopFee,
    decimal? WeightLimitKg,
    decimal WeightSurchargePerKg,
    string? SizeLimit,
    string? Types,
    decimal? LongDistanceBaseFare,
    decimal? LongDistancePerKm41to60,
    decimal? LongDistancePerKmAbove60,
    string? SurchargeInfo,
    string? Remarks,
    Guid? ChangedByUserId,
    string? ChangedByUserName,
    DateTime CreatedAt
);
