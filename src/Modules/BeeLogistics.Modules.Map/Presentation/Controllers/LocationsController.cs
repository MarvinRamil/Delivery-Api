using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Handlers;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.DTOs;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Map.Presentation.Controllers;

[Route("api/locations")]
public class LocationsController : BaseController
{
    /// <summary>
    /// Upper bound on a single batch. The driver app flushes at 10 buffered points
    /// (locationTrackingService.ts BATCH_SIZE_LIMIT), so this is generous headroom for a long
    /// offline stretch while still capping the work one request can queue: every point costs a
    /// sanitizer pass and a RabbitMQ publish, and the list arrived unbounded from the client.
    /// </summary>
    private const int MaxBatchSize = 200;

    private readonly IMediator _mediator;

    public LocationsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("update")]
    public async Task<IActionResult> UpdateLocation([FromBody] UpdateLocationDto dto, CancellationToken ct)
    {
        if (!TryGetDriverId(out var driverId))
            return Unauthorized("Driver ID not found in token");

        var command = new UpdateDriverLocationCommand(driverId, dto);
        var result = await _mediator.Send(command, ct);

        return FromResult(result);
    }

    /// <summary>
    /// Backfill points the driver app buffered while it could not reach the MQTT broker, which is
    /// the normal state once the app is backgrounded.
    ///
    /// These are historical by definition, so they are written to history only and do not disturb
    /// the driver's live position or the customer's tracking view. Send <c>recordedAt</c> on each
    /// item: without it the server can only stamp the flush instant, and the whole trail collapses
    /// into one moment.
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> UpdateLocationBatch([FromBody] List<UpdateLocationDto> dtos, CancellationToken ct)
    {
        if (!TryGetDriverId(out var driverId))
            return Unauthorized("Driver ID not found in token");

        if (dtos is null || dtos.Count == 0)
            return BadRequest(ApiResponse.Fail("At least one location is required"));

        if (dtos.Count > MaxBatchSize)
            return BadRequest(ApiResponse.Fail($"Batch size {dtos.Count} exceeds the maximum of {MaxBatchSize}"));

        // Oldest first, regardless of what order the client sent. The sanitizer's teleport check
        // compares consecutive points, so a shuffled batch would read as a series of impossible
        // jumps. Items without a recordedAt sort last: they can only be stamped "now".
        var ordered = dtos
            .OrderBy(d => d.RecordedAt ?? DateTime.MaxValue)
            .ToList();

        var accepted = 0;
        var rejections = new List<string>();

        foreach (var dto in ordered)
        {
            var result = await _mediator.Send(new UpdateDriverLocationCommand(driverId, dto, IsHistorical: true), ct);

            if (result.IsSuccess)
                accepted++;
            else
                rejections.Add(result.Error ?? "Rejected");
        }

        // Report what actually happened. This endpoint used to return the number of points it was
        // handed without ever inspecting the results, so the app cleared its buffer on a response
        // that said nothing about whether anything had been stored.
        return Ok(ApiResponse<LocationBatchResultDto>.Ok(new LocationBatchResultDto(
            Received: ordered.Count,
            Accepted: accepted,
            Rejected: rejections.Count,
            // Bounded: a fully-rejected 200-point batch is one repeated cause, not 200 stories.
            Errors: rejections.Distinct().Take(5).ToList())));
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentLocation(CancellationToken ct)
    {
        if (!TryGetDriverId(out var driverId))
            return Unauthorized("Driver ID not found in token");

        var query = new GetDriverLocationQuery(driverId);
        var result = await _mediator.Send(query, ct);

        return FromResult(result);
    }

    /// <summary>
    /// The caller's recorded location trail, newest first.
    ///
    /// Reads the trail table. It previously read the current-position snapshot, which holds one row
    /// per driver, so it returned a single point whatever range was requested (GitLab #39). The
    /// response shape is unchanged.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetLocationHistory([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int limit = 500, CancellationToken ct = default)
    {
        if (!TryGetDriverId(out var driverId))
            return Unauthorized("Driver ID not found in token");

        var query = new GetDriverLocationHistoryQuery(driverId, from, to, limit);
        var result = await _mediator.Send(query, ct);

        return FromResult(result);
    }

    [HttpGet("nearby")]
    public async Task<IActionResult> GetNearbyDrivers([FromQuery] decimal latitude, [FromQuery] decimal longitude, [FromQuery] decimal radiusKm = 10, CancellationToken ct = default)
    {
        var query = new GetLocationsWithinRadiusQuery(latitude, longitude, radiusKm);
        var result = await _mediator.Send(query, ct);

        return FromResult(result);
    }

    [HttpGet("driver/{driverId}")]
    [Authorize(Roles = "Admin,Owner,Dispatcher")]
    public async Task<IActionResult> GetDriverLocation(Guid driverId, CancellationToken ct)
    {
        var query = new GetDriverLocationQuery(driverId);
        var result = await _mediator.Send(query, ct);

        return FromResult(result);
    }

    [HttpGet("within-radius")]
    public async Task<IActionResult> GetLocationsWithinRadius([FromQuery] decimal latitude, [FromQuery] decimal longitude, [FromQuery] decimal radiusKm, CancellationToken ct = default)
    {
        var query = new GetLocationsWithinRadiusQuery(latitude, longitude, radiusKm);
        var result = await _mediator.Send(query, ct);

        return FromResult(result);
    }

    /// <summary>
    /// The driver is always the authenticated caller - never a value from the request body.
    /// ASP.NET Core maps the JWT "sub" claim to ClaimTypes.NameIdentifier automatically; the other
    /// two are fallbacks for tokens that were issued differently.
    /// </summary>
    private bool TryGetDriverId(out Guid driverId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? User.FindFirst("sub")?.Value
                    ?? User.FindFirst("id")?.Value;

        return Guid.TryParse(claim, out driverId);
    }
}

/// <param name="Received">Points in the request, after the size check.</param>
/// <param name="Accepted">Points published for processing.</param>
/// <param name="Rejected">Points refused by validation. The client should not assume these are stored.</param>
/// <param name="Errors">Distinct rejection reasons, capped, for diagnosis.</param>
public record LocationBatchResultDto(
    int Received,
    int Accepted,
    int Rejected,
    IReadOnlyList<string> Errors
);
