using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Drivers.Infrastructure.Repositories;

public class SavedWithdrawalMethodRepository : Repository<SavedWithdrawalMethod, DriversDbContext>, ISavedWithdrawalMethodRepository
{
    // AccountNumber is encrypted at rest transparently by the EF value converter
    // (see DriversDbContext: SavedWithdrawalMethod.AccountNumber -> piiConverter). The entity
    // always holds plaintext in memory; no manual encrypt/decrypt is needed here.
    public SavedWithdrawalMethodRepository(DriversDbContext context)
        : base(context)
    {
    }

    public async Task<IReadOnlyList<SavedWithdrawalMethod>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(wm => wm.DriverId == driverId)
            .OrderByDescending(wm => wm.IsDefault)
            .ThenByDescending(wm => wm.LastUsedAt)
            .ThenByDescending(wm => wm.CreatedAt)
            .ToListAsync(ct);
    }

    public new async Task<SavedWithdrawalMethod?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await DbSet.FirstOrDefaultAsync(wm => wm.Id == id, ct);
    }

    public async Task<SavedWithdrawalMethod?> GetDefaultAsync(Guid driverId, CancellationToken ct = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(wm => wm.DriverId == driverId && wm.IsDefault, ct);
    }

    public async Task UnsetAllDefaultsAsync(Guid driverId, CancellationToken ct = default)
    {
        var defaults = await DbSet
            .Where(wm => wm.DriverId == driverId && wm.IsDefault)
            .ToListAsync(ct);

        foreach (var method in defaults)
        {
            method.UnsetAsDefault();
        }

        // Note: SaveChangesAsync should be called by the caller after this method
    }
}
