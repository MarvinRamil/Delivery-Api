using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Notification.Presentation.Controllers;

/// <summary>
/// Read-only history of push notifications sent (Queued → Sent/Failed). Backs the
/// backoffice "Sent Notifications" page. Mirrors EmailStatusController.
/// </summary>
[Route("api/notifications/history")]
[Authorize(Roles = "SuperAdmin,Admin")]
public class PushStatusController : ControllerBase
{
    private readonly IPushRecordRepository _pushRecordRepository;

    public PushStatusController(IPushRecordRepository pushRecordRepository)
    {
        _pushRecordRepository = pushRecordRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] PushStatus? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _pushRecordRepository.GetPagedAsync(status, search, page, pageSize, ct);

        var records = items.Select(p => new
        {
            p.Id,
            p.Title,
            p.Body,
            p.TargetMode,
            p.TargetValue,
            p.AppType,
            Status = p.Status.ToString(),
            p.DevicesSent,
            p.ErrorMessage,
            p.Source,
            p.CreatedAt,
            p.SentAt,
            p.FailedAt
        }).ToList();

        return Ok(new
        {
            success = true,
            data = records,
            pagination = new
            {
                page,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            }
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct = default)
    {
        var stats = await _pushRecordRepository.GetStatsAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                total = stats.Total,
                last24Hours = stats.Last24Hours,
                byStatus = stats.ByStatus
            }
        });
    }
}
