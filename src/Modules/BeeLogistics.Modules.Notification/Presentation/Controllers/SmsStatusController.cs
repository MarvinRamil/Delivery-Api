using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Notification.Presentation.Controllers;

[Route("api/sms")]
[Authorize(Roles = "SuperAdmin,Admin")]
public class SmsStatusController : ControllerBase
{
    private readonly ISmsRecordRepository _smsRecordRepository;

    public SmsStatusController(ISmsRecordRepository smsRecordRepository)
    {
        _smsRecordRepository = smsRecordRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetSmsStatus(
        [FromQuery] SmsStatus? status = null,
        [FromQuery] string? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _smsRecordRepository.GetPagedAsync(status, to, page, pageSize, ct);

        var records = items.Select(s => new
        {
            s.Id,
            s.To,
            s.MessageType,
            s.Provider,
            Status = s.Status.ToString(),
            s.ErrorMessage,
            s.CreatedAt,
            s.SentAt
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
}
