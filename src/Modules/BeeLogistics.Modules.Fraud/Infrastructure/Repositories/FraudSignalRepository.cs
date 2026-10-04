using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Fraud.Infrastructure.Repositories;

public class FraudSignalRepository : IFraudSignalRepository
{
    private readonly FraudDbContext _context;

    public FraudSignalRepository(FraudDbContext context)
    {
        _context = context;
    }

    public void Add(FraudSignal signal) => _context.FraudSignals.Add(signal);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<FraudSignal>> GetSignalsAsync(DateTime? from, DateTime? to, string? ruleName, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _context.FraudSignals.AsQueryable();
        if (from.HasValue) query = query.Where(s => s.DetectedAt >= from.Value);
        if (to.HasValue) query = query.Where(s => s.DetectedAt <= to.Value);
        if (!string.IsNullOrEmpty(ruleName)) query = query.Where(s => s.RuleName == ruleName);
        return await query
            .OrderByDescending(s => s.DetectedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public async Task<int> CountSignalsAsync(DateTime? from, DateTime? to, string? ruleName, CancellationToken ct = default)
    {
        var query = _context.FraudSignals.AsQueryable();
        if (from.HasValue) query = query.Where(s => s.DetectedAt >= from.Value);
        if (to.HasValue) query = query.Where(s => s.DetectedAt <= to.Value);
        if (!string.IsNullOrEmpty(ruleName)) query = query.Where(s => s.RuleName == ruleName);
        return await query.CountAsync(ct);
    }
}
