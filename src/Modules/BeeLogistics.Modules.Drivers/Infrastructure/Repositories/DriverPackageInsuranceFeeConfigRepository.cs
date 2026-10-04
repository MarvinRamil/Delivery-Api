using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Drivers.Infrastructure.Repositories;

public class DriverPackageInsuranceFeeConfigRepository : Repository<DriverPackageInsuranceFeeConfig, DriversDbContext>, IDriverPackageInsuranceFeeConfigRepository
{
    public DriverPackageInsuranceFeeConfigRepository(DriversDbContext context) : base(context) { }

    public async Task<DriverPackageInsuranceFeeConfig?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default)
        => await DbSet
            .Include(c => c.Versions.OrderByDescending(v => v.Version))
            .FirstOrDefaultAsync(c => c.VehicleType == vehicleType && !c.IsDeleted, ct);

    public async Task<IReadOnlyList<DriverPackageInsuranceFeeConfigVersion>> GetVersionsByConfigIdAsync(Guid driverPackageInsuranceFeeConfigId, CancellationToken ct = default)
        => await Context.Set<DriverPackageInsuranceFeeConfigVersion>()
            .Where(v => v.DriverPackageInsuranceFeeConfigId == driverPackageInsuranceFeeConfigId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
}
