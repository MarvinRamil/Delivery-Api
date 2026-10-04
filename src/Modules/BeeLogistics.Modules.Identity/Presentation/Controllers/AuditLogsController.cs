using BeeLogistics.Modules.Identity.Application;
using BeeLogistics.Modules.Identity.Application.Interfaces;
using BeeLogistics.Shared.Infrastructure.Security;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

[Route("api/audit-logs")]
[Authorize(Policy = "Backoffice")]
public class AuditLogsController : BaseController
{
    private readonly IAuditService _auditService;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AuditLogsController> _logger;

    public AuditLogsController(IAuditService auditService, IAuditLogRepository auditLogRepository, ILogger<AuditLogsController> logger)
    {
        _auditService = auditService;
        _auditLogRepository = auditLogRepository;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetLogs(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? category = null,
        [FromQuery] string? action = null,
        [FromQuery] string? userId = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        limit = Math.Min(limit, 200);

        var logs = await _auditService.GetLogsAsync(from, to, category, action, userId, limit, offset);
        var total = await _auditService.GetLogCountAsync(from, to, category, action, userId);

        return Ok(new
        {
            success = true,
            data = logs.Select(l => new
            {
                l.Id,
                l.Action,
                l.Category,
                l.UserId,
                // SECURITY: PII already masked at write; mask again on read for legacy records
                UserName = PiiMasker.MaskName(l.UserName),
                UserEmail = PiiMasker.MaskEmail(l.UserEmail),
                l.UserRole,
                l.EntityId,
                l.EntityType,
                l.Details,
                l.IpAddress,
                l.IsSuccess,
                l.ErrorMessage,
                l.Timestamp
            }),
            total,
            limit,
            offset
        });
    }

    [HttpPost("{id}/archive")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ArchiveLog(Guid id)
    {
        // SECURITY: Additional authorization check (defense-in-depth)
        var isBackofficeClaim = User.FindFirst("is_backoffice")?.Value == "true";
        var roleClaim = User.FindFirst("role")?.Value;
        
        if (!isBackofficeClaim || roleClaim != "SuperAdmin")
        {
            _logger.LogWarning(
                "Unauthorized audit log archive attempt by user {UserId} with role {Role}, is_backoffice: {IsBackoffice}",
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                roleClaim,
                isBackofficeClaim);
            return Forbid("Access denied. Archive operations are restricted to SuperAdmin only.");
        }

        var archived = await _auditLogRepository.ArchiveAsync(id);
        if (!archived)
            return NotFound(new { success = false, message = "Log not found" });

        return Ok(new { success = true, message = "Log archived" });
    }

    [HttpPost("archive-before")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ArchiveLogsBefore([FromQuery] DateTime before)
    {
        // SECURITY: Additional authorization check (defense-in-depth)
        var isBackofficeClaim = User.FindFirst("is_backoffice")?.Value == "true";
        var roleClaim = User.FindFirst("role")?.Value;
        
        if (!isBackofficeClaim || roleClaim != "SuperAdmin")
        {
            _logger.LogWarning(
                "Unauthorized audit log archive-before attempt by user {UserId} with role {Role}, is_backoffice: {IsBackoffice}",
                User.FindFirstValue(ClaimTypes.NameIdentifier),
                roleClaim,
                isBackofficeClaim);
            return Forbid("Access denied. Archive operations are restricted to SuperAdmin only.");
        }

        var count = await _auditLogRepository.ArchiveBeforeAsync(before);

        return Ok(new { success = true, message = $"Archived {count} logs" });
    }
}
