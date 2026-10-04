using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Revenue.Infrastructure.Repositories;

public class PlatformCommissionRepository : IPlatformCommissionRepository
{
    private readonly RevenueDbContext _context;

    public PlatformCommissionRepository(RevenueDbContext context)
    {
        _context = context;
    }

    public async Task<PlatformCommission?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await _context.PlatformCommissions
            .FirstOrDefaultAsync(c => c.BookingId == bookingId, ct);
    }

    public async Task<PlatformCommission> CreateAsync(PlatformCommission commission, CancellationToken ct = default)
    {
        _context.PlatformCommissions.Add(commission);
        await _context.SaveChangesAsync(ct);
        return commission;
    }

    public async Task<IReadOnlyList<PlatformCommission>> GetByDriverIdAsync(Guid driverId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var query = _context.PlatformCommissions
            .Where(c => c.DriverId == driverId);

        if (from.HasValue)
            query = query.Where(c => c.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(c => c.CreatedAt <= to.Value);

        return await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformCommission>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _context.PlatformCommissions
            .Where(c => c.CreatedAt >= from && c.CreatedAt <= to)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
