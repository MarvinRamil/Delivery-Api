using BeeLogistics.Modules.Identity.Application.Interfaces;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Identity.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly IdentityAppDbContext _context;

    public AuditLogRepository(IdentityAppDbContext context)
    {
        _context = context;
    }

    public async Task<AuditLog?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.AuditLogs.FindAsync([id], ct);

    public async Task SaveChangesAsync(CancellationToken ct = default)
        => await _context.SaveChangesAsync(ct);

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var log = await _context.AuditLogs.FindAsync([id], ct);
        if (log == null) return false;
        log.IsArchived = true;
        log.ArchivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> ArchiveBeforeAsync(DateTime before, CancellationToken ct = default)
        => await _context.AuditLogs
            .Where(l => !l.IsArchived && l.Timestamp < before)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsArchived, true)
                .SetProperty(l => l.ArchivedAt, DateTime.UtcNow), ct);
}
