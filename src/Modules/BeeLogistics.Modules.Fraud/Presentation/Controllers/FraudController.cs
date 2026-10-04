using BeeLogistics.Modules.Fraud.Application.DTOs;
using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Fraud.Presentation.Controllers;

[ApiController]
[Route("api/fraud")]
[Authorize(Policy = "Backoffice")]
public class FraudController : ControllerBase
{
    private readonly IFraudEventRepository _eventRepo;
    private readonly IFraudSignalRepository _signalRepo;

    public FraudController(IFraudEventRepository eventRepo, IFraudSignalRepository signalRepo)
    {
        _eventRepo = eventRepo;
        _signalRepo = signalRepo;
    }

    /// <summary>
    /// Get collected fraud events (data used for rules and future ML).
    /// </summary>
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] FraudEventType? eventType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 500);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = await _eventRepo.GetEventsAsync(from, to, eventType, page, pageSize, ct);
        var total = await _eventRepo.CountEventsAsync(from, to, eventType, ct);
        var dtos = items.Select(e => new FraudEventDto(
            e.Id, e.EventType, e.ActorId, e.OccurredAt, e.BookingId, e.DriverId, e.CustomerId, e.UserId, e.DeviceId,
            e.Latitude, e.Longitude, e.PreviousLatitude, e.PreviousLongitude, e.DistanceKm, e.MetadataJson, e.CreatedAt));
        return Ok(new { items = dtos, totalCount = total, page, pageSize });
    }

    /// <summary>
    /// Get fraud signals (suspicious activity detected by rules).
    /// </summary>
    [HttpGet("signals")]
    public async Task<IActionResult> GetSignals(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? ruleName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 500);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var items = await _signalRepo.GetSignalsAsync(from, to, ruleName, page, pageSize, ct);
        var total = await _signalRepo.CountSignalsAsync(from, to, ruleName, ct);
        var dtos = items.Select(s => new FraudSignalDto(
            s.Id, s.RuleName, s.ActorId, s.Severity, s.DetectedAt, s.MetadataJson, s.ResolvedAt, s.Label, s.CreatedAt));
        return Ok(new { items = dtos, totalCount = total, page, pageSize });
    }
}
