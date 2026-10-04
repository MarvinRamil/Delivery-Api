using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

public class VehiclePricingRepository : Repository<VehiclePricing, BookingsDbContext>, IVehiclePricingRepository
{
    public VehiclePricingRepository(BookingsDbContext context) : base(context) { }

    public async Task<VehiclePricing?> GetByVehicleTypeAsync(string vehicleType, CancellationToken ct = default)
        => await DbSet
            .Include(v => v.Versions.OrderByDescending(v => v.Version))
            .FirstOrDefaultAsync(v => v.VehicleType == vehicleType && v.IsActive && !v.IsDeleted, ct);

    public async Task<IReadOnlyList<VehiclePricing>> GetActivePricingsAsync(CancellationToken ct = default)
        => await DbSet
            .Where(v => v.IsActive && !v.IsDeleted)
            .OrderBy(v => v.VehicleType)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<VehiclePricingVersion>> GetVersionsByVehiclePricingIdAsync(Guid vehiclePricingId, CancellationToken ct = default)
    {
        return await Context.Set<VehiclePricingVersion>()
            .Where(v => v.VehiclePricingId == vehiclePricingId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
    }

    public async Task<VehiclePricingVersion?> GetVersionAsync(Guid vehiclePricingId, int version, CancellationToken ct = default)
    {
        return await Context.Set<VehiclePricingVersion>()
            .FirstOrDefaultAsync(v => v.VehiclePricingId == vehiclePricingId && v.Version == version, ct);
    }
}
