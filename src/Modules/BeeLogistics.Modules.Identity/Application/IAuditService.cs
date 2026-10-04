using BeeLogistics.Modules.Identity.Domain;

namespace BeeLogistics.Modules.Identity.Application;

public interface IAuditService
{
    Task LogAsync(
        string action,
        string category,
        string? userId = null,
        string? userName = null,
        string? userEmail = null,
        string? userRole = null,
        string? entityId = null,
        string? entityType = null,
        string? details = null,
        bool isSuccess = true,
        string? errorMessage = null,
        string? userAgent = null,
        string? requestId = null);

    Task<IEnumerable<AuditLog>> GetLogsAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? category = null,
        string? action = null,
        string? userId = null,
        int limit = 100,
        int offset = 0);
    
    Task<int> GetLogCountAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? category = null,
        string? action = null,
        string? userId = null);
}
