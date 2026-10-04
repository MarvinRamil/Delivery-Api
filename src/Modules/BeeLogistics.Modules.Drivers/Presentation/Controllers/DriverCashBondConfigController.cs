using System.Security.Claims;
using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Drivers.Presentation.Controllers;

/// <summary>
/// Admin CRUD for the per-vehicle-type cashbond amount (issue #103). Not anonymous like
/// VehiclePricingController's GETs — cashbond rates aren't public pricing.
/// </summary>
[Authorize(Policy = "Backoffice")]
[ApiController]
[Route("api/driver-cashbond-config")]
public class DriverCashBondConfigController : BaseController
{
    private readonly IMediator _mediator;

    public DriverCashBondConfigController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => FromResult(await _mediator.Send(new GetDriverCashBondConfigsQuery(), ct));

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> GetVersions(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetDriverCashBondConfigVersionsQuery(id), ct));

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertDriverCashBondConfigDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.FindFirstValue("full_name") ?? User.FindFirstValue(ClaimTypes.Name);
        var userIdGuid = Guid.TryParse(userId, out var parsedId) ? parsedId : (Guid?)null;

        var command = new CreateOrUpdateDriverCashBondConfigCommand(dto.VehicleType, dto.Amount, userIdGuid, userName);
        return FromResult(await _mediator.Send(command, ct));
    }
}
