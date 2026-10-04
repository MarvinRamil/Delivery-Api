using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

public interface IDriverCashBondConfigRepository : IRepository<DriverCashBondConfig>
{
    Task<DriverCashBondConfig?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default);
    Task<IReadOnlyList<DriverCashBondConfigVersion>> GetVersionsByConfigIdAsync(Guid driverCashBondConfigId, CancellationToken ct = default);
}
