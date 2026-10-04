using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

public interface IVehiclePricingRepository : IRepository<VehiclePricing>
{
    Task<VehiclePricing?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default);
    Task<IReadOnlyList<VehiclePricing>> GetActivePricingsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VehiclePricingVersion>> GetVersionsByVehiclePricingIdAsync(Guid vehiclePricingId, CancellationToken ct = default);
    Task<VehiclePricingVersion?> GetVersionAsync(Guid vehiclePricingId, int version, CancellationToken ct = default);
}
