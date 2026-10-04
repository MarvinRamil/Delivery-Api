using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Infrastructure;

public class AuditService : IAuditService
{
    private readonly IdentityAppDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(
        IdentityAppDbContext context,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditService> logger)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task LogAsync(
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
        string? requestId = null)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            
            // SECURITY: Sanitize details and errorMessage to prevent logging sensitive data
            // Never log passwords, tokens, or secrets
            var sanitizedDetails = SanitizeSensitiveData(details);
            var sanitizedErrorMessage = SanitizeSensitiveData(errorMessage);
            
            // Get user agent from parameter or HTTP context
            var finalUserAgent = userAgent ?? httpContext?.Request.Headers["User-Agent"].FirstOrDefault();
            
            // Get request ID from parameter or HTTP context items
            string? finalRequestId = requestId;
            if (string.IsNullOrEmpty(finalRequestId) && httpContext?.Items.TryGetValue("RequestId", out var reqId) == true)
            {
                finalRequestId = reqId?.ToString();
            }
            
            // DPA: Store masked PII in audit log so the DB does not hold full names/emails
            var auditLog = new AuditLog
            {
                Action = action,
                Category = category,
                UserId = userId,
                UserName = PiiMasker.MaskName(userName),
                UserEmail = PiiMasker.MaskEmail(userEmail),
                UserRole = userRole,
                EntityId = entityId,
                EntityType = entityType,
                Details = sanitizedDetails,
                IsSuccess = isSuccess,
                ErrorMessage = sanitizedErrorMessage,
                IpAddress = GetClientIpAddress(httpContext),
                UserAgent = finalUserAgent,
                RequestId = finalRequestId,
                Timestamp = DateTime.UtcNow
            };

            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log: {Action} - {Category}", action, category);
        }
    }

    // SECURITY: Regex timeout to prevent ReDoS attacks (1 second max)
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sanitizes sensitive data from log entries (passwords, tokens, secrets)
    /// </summary>
    private static string? SanitizeSensitiveData(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        try
        {
            // Remove common sensitive patterns
            var sanitized = input;
            
            // Remove JWT tokens (base64 encoded, typically 100+ chars)
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized, 
                @"eyJ[A-Za-z0-9_-]{100,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}", 
                "[JWT_TOKEN_REDACTED]",
                System.Text.RegularExpressions.RegexOptions.None,
                RegexTimeout);
            
            // Remove password patterns
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized, 
                @"(?i)(password|pwd|pass)\s*[:=]\s*[^\s,}]+", 
                "[PASSWORD_REDACTED]",
                System.Text.RegularExpressions.RegexOptions.None,
                RegexTimeout);
            
            // Remove token patterns
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized, 
                @"(?i)(token|secret|key|apikey)\s*[:=]\s*[^\s,}]+", 
                "[SECRET_REDACTED]",
                System.Text.RegularExpressions.RegexOptions.None,
                RegexTimeout);

            return sanitized;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // If regex times out, return redacted message to be safe
            return "[CONTENT_REDACTED_TIMEOUT]";
        }
    }

    public async Task<IEnumerable<AuditLog>> GetLogsAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? category = null,
        string? action = null,
        string? userId = null,
        int limit = 100,
        int offset = 0)
    {
        var query = _context.AuditLogs.Where(l => !l.IsArchived);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);
        if (!string.IsNullOrEmpty(category))
            query = query.Where(l => l.Category == category);
        if (!string.IsNullOrEmpty(action))
            query = query.Where(l => l.Action == action);
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(l => l.UserId == userId);

        return await query
            .OrderByDescending(l => l.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> GetLogCountAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? category = null,
        string? action = null,
        string? userId = null)
    {
        var query = _context.AuditLogs.Where(l => !l.IsArchived);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);
        if (to.HasValue)
            query = query.Where(l => l.Timestamp <= to.Value);
        if (!string.IsNullOrEmpty(category))
            query = query.Where(l => l.Category == category);
        if (!string.IsNullOrEmpty(action))
            query = query.Where(l => l.Action == action);
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(l => l.UserId == userId);

        return await query.CountAsync();
    }

    private static string? GetClientIpAddress(HttpContext? context)
    {
        if (context == null) return null;

        // Check for forwarded IP (behind proxy/load balancer)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').FirstOrDefault()?.Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}

