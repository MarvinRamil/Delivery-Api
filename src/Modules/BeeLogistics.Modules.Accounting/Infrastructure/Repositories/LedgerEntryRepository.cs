using BeeLogistics.Modules.Accounting.Application.Interfaces;
using BeeLogistics.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Accounting.Infrastructure.Repositories;

public class LedgerEntryRepository : ILedgerEntryRepository
{
    private readonly AccountingDbContext _context;

    public LedgerEntryRepository(AccountingDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(LedgerEntry entry, CancellationToken ct = default)
    {
        await _context.LedgerEntries.AddAsync(entry, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IEnumerable<LedgerEntry> entries, CancellationToken ct = default)
    {
        await _context.LedgerEntries.AddRangeAsync(entries, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetByReferenceAsync(string referenceType, string referenceId, CancellationToken ct = default)
    {
        return await _context.LedgerEntries
            .Where(e => e.ReferenceType == referenceType && e.ReferenceId == referenceId)
            .OrderBy(e => e.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetAsync(DateTime? fromUtc = null, DateTime? toUtc = null, string? accountCode = null, int limit = 500, CancellationToken ct = default)
    {
        var query = _context.LedgerEntries.AsQueryable();
        if (fromUtc.HasValue)
            query = query.Where(e => e.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue)
            query = query.Where(e => e.CreatedAtUtc <= toUtc.Value);
        if (!string.IsNullOrWhiteSpace(accountCode))
            query = query.Where(e => e.AccountCode == accountCode);
        var capped = Math.Clamp(limit, 1, 500);
        return await query
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(capped)
            .ToListAsync(ct);
    }

    public async Task<decimal> GetBalanceAsync(string accountCode, CancellationToken ct = default)
    {
        // Summed in SQL. Previously every entry for the account was materialised into memory and
        // summed in C#, so the cost grew without bound as the ledger did - and the ledger only
        // ever grows.
        return await _context.LedgerEntries
            .Where(e => e.AccountCode == accountCode)
            .SumAsync(e => e.IsDebit ? e.Amount : -e.Amount, ct);
    }

    public async Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return false;
        return await _context.LedgerEntries
            .AnyAsync(e => e.IdempotencyKey == idempotencyKey, ct);
    }
}
