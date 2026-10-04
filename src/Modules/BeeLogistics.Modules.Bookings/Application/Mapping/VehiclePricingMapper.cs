using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Helper
internal static class VehiclePricingMapper
{
    public static VehiclePricingDto ToDto(VehiclePricing pricing) =>
        new(
            pricing.Id,
            pricing.VehicleType,
            pricing.Types,
            pricing.IsActive,
            pricing.BaseFare,
            pricing.PerKm0to5,
            pricing.PerKmAbove5,
            pricing.AdditionalStopFee,
            pricing.WeightLimitKg,
            pricing.WeightSurchargePerKg,
            pricing.SizeLimit,
            pricing.LongDistanceBaseFare,
            pricing.LongDistancePerKm41to60,
            pricing.LongDistancePerKmAbove60,
            pricing.SurchargeInfo,
            pricing.Remarks,
            pricing.Version,
            pricing.UpdatedByUserId,
            pricing.UpdatedByUserName,
            pricing.CreatedAt,
            pricing.UpdatedAt
        );
}
