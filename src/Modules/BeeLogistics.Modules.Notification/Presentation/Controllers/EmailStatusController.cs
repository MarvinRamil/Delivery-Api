using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Notification.Presentation.Controllers;

[Route("api/emails")]
[Authorize(Roles = "SuperAdmin,Admin")]
public class EmailStatusController : ControllerBase
{
    private readonly IEmailRecordRepository _emailRecordRepository;

    public EmailStatusController(IEmailRecordRepository emailRecordRepository)
    {
        _emailRecordRepository = emailRecordRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetEmailStatus(
        [FromQuery] EmailStatus? status = null,
        [FromQuery] string? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _emailRecordRepository.GetPagedAsync(status, to, page, pageSize, ct);

        var emails = items.Select(e => new
        {
            e.Id,
            e.To,
            e.Subject,
            e.From,
            e.FromName,
            Status = e.Status.ToString(),
            e.RetryCount,
            e.ErrorMessage,
            e.CreatedAt,
            e.SentAt,
            e.FailedAt,
            e.HangfireJobId
        }).ToList();

        return Ok(new
        {
            success = true,
            data = emails,
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
    public async Task<IActionResult> GetEmailStats(CancellationToken ct = default)
    {
        var stats = await _emailRecordRepository.GetStatsAsync(ct);

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
