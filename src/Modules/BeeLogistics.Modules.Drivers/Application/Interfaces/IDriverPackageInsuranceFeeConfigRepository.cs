using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

public interface IDriverPackageInsuranceFeeConfigRepository : IRepository<DriverPackageInsuranceFeeConfig>
{
    Task<DriverPackageInsuranceFeeConfig?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default);
    Task<IReadOnlyList<DriverPackageInsuranceFeeConfigVersion>> GetVersionsByConfigIdAsync(Guid driverPackageInsuranceFeeConfigId, CancellationToken ct = default);
}
