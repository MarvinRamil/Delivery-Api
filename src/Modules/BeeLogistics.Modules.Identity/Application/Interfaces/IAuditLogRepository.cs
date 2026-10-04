using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Identity.Application.Interfaces;

/// <summary>
/// Repository for AuditLog. Keeps data access in Infrastructure; Presentation uses this via Application layer.
/// </summary>
public interface IAuditLogRepository
{
    Task<AuditLog?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    /// <summary>Archive a single log by id. Returns true if found and archived.</summary>
    Task<bool> ArchiveAsync(Guid id, CancellationToken ct = default);
    /// <summary>Archive all logs with Timestamp before the given date. Returns count archived.</summary>
    Task<int> ArchiveBeforeAsync(DateTime before, CancellationToken ct = default);
}
