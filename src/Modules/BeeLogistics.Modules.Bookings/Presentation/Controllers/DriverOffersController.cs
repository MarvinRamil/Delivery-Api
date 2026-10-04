using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

[Route("api/driver-offers")]
[Authorize]
public class DriverOffersController : BaseController
{
    private readonly IMediator _mediator;
    private readonly ILogger<DriverOffersController> _logger;

    public DriverOffersController(IMediator mediator, ILogger<DriverOffersController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct, [FromQuery] int limit = 3)
    {
        _logger.LogInformation("[DriverOffers] GET /api/driver-offers/pending hit. Limit={Limit}", limit);

        var driverIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(driverIdClaim) || !Guid.TryParse(driverIdClaim, out var driverId))
        {
            _logger.LogWarning("[DriverOffers] GET pending: Unauthorized - Driver ID not found in token");
            return Unauthorized("Driver ID not found in token");
        }

        _logger.LogInformation("[DriverOffers] GET pending: DriverId={DriverId}, Limit={Limit}", driverId, limit);
        return FromResult(await _mediator.Send(new GetPendingOffersQuery(driverId, limit), ct));
    }

    [HttpPost("{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
    {
        var driverIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(driverIdClaim) || !Guid.TryParse(driverIdClaim, out var driverId))
            return Unauthorized("Driver ID not found in token");

        return FromResult(await _mediator.Send(new AcceptBookingOfferCommand(id, driverId), ct));
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var driverIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(driverIdClaim) || !Guid.TryParse(driverIdClaim, out var driverId))
            return Unauthorized("Driver ID not found in token");

        return FromResult(await _mediator.Send(new RejectBookingOfferCommand(id, driverId), ct));
    }
}
