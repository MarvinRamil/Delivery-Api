using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Accounting.Infrastructure.Repositories;

public class SalesEntryRepository : ISalesEntryRepository
{
    private readonly AccountingDbContext _context;

    public SalesEntryRepository(AccountingDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SalesEntry entry, CancellationToken ct = default)
    {
        await _context.SalesEntries.AddAsync(entry, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<SalesEntry?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await _context.SalesEntries
            .FirstOrDefaultAsync(e => e.BookingId == bookingId, ct);
    }

    public async Task<IReadOnlyList<SalesEntry>> GetAsync(DateTime? fromUtc = null, DateTime? toUtc = null, string? paymentMethod = null, int limit = 500, CancellationToken ct = default)
    {
        var query = _context.SalesEntries.AsQueryable();
        if (fromUtc.HasValue)
            query = query.Where(e => e.CompletedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue)
            query = query.Where(e => e.CompletedAtUtc <= toUtc.Value);
        if (!string.IsNullOrWhiteSpace(paymentMethod))
            query = query.Where(e => e.PaymentMethod == paymentMethod);
        var capped = Math.Clamp(limit, 1, 500);
        return await query
            .OrderByDescending(e => e.CompletedAtUtc)
            .Take(capped)
            .ToListAsync(ct);
    }
}
