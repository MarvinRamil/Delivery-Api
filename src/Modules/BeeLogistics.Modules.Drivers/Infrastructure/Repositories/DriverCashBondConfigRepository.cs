using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Drivers.Infrastructure.Repositories;

public class DriverCashBondConfigRepository : Repository<DriverCashBondConfig, DriversDbContext>, IDriverCashBondConfigRepository
{
    public DriverCashBondConfigRepository(DriversDbContext context) : base(context) { }

    public async Task<DriverCashBondConfig?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default)
        => await DbSet
            .Include(c => c.Versions.OrderByDescending(v => v.Version))
            .FirstOrDefaultAsync(c => c.VehicleType == vehicleType && !c.IsDeleted, ct);

    public async Task<IReadOnlyList<DriverCashBondConfigVersion>> GetVersionsByConfigIdAsync(Guid driverCashBondConfigId, CancellationToken ct = default)
        => await Context.Set<DriverCashBondConfigVersion>()
            .Where(v => v.DriverCashBondConfigId == driverCashBondConfigId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
}
