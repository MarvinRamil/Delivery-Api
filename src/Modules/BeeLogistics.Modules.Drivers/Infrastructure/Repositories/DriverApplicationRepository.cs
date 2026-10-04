using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Drivers.Infrastructure.Repositories;

public class DriverApplicationRepository : Repository<DriverApplication, DriversDbContext>, IDriverApplicationRepository
{
    private readonly IDataProtectorService _protector;

    public DriverApplicationRepository(DriversDbContext context, IDataProtectorService protector) : base(context)
    {
        _protector = protector;
    }

    // A driver can have multiple applications (each resubmission is a new row),
    // so these lookups return the most recent one by CreatedAt.
    public async Task<DriverApplication?> GetByUserIdAsync(string userId, CancellationToken ct = default)
        => await DbSet.Where(x => x.UserId == userId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<DriverApplication?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        // Email is encrypted (non-deterministic) so we match on its deterministic blind index.
        var emailHash = _protector.ComputeBlindIndex(email, "email");
        return await DbSet.Where(
                x => EF.Property<string>(x, "EmailHash") == emailHash && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DriverApplication?> GetByUserIdOrEmailAsync(string? userId, string email, CancellationToken ct = default)
    {
        var emailHash = _protector.ComputeBlindIndex(email, "email");
        return await DbSet.Where(
                x => (userId != null && x.UserId == userId || EF.Property<string>(x, "EmailHash") == emailHash) && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DriverApplication>> GetAllByStatusAsync(DriverApplicationStatus? status, CancellationToken ct = default)
    {
        var query = DbSet.Where(x => !x.IsDeleted);
        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);
        return await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    }
}
