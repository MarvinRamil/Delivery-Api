using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

[Route("api/vehicle-pricing")]
public class VehiclePricingController : BaseController
{
    private readonly IMediator _mediator;

    public VehiclePricingController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [AllowAnonymous] // Public endpoint for pricing page
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => FromResult(await _mediator.Send(new GetVehiclePricingsQuery(), ct));

    [HttpGet("{id:guid}")]
    [AllowAnonymous] // Public endpoint for pricing page
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetVehiclePricingByIdQuery(id), ct));

    [HttpGet("vehicle-type/{vehicleType}")]
    [AllowAnonymous] // Public endpoint for pricing page
    public async Task<IActionResult> GetByVehicleType(string vehicleType, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetVehiclePricingByVehicleTypeQuery(vehicleType), ct));

    [HttpGet("{id:guid}/versions")]
    [Authorize(Policy = "Backoffice")] // Admin only for version history
    public async Task<IActionResult> GetVersions(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetVehiclePricingVersionsQuery(id), ct));

    [HttpPost]
    [Authorize(Policy = "Backoffice")] // Admin only for CRUD operations
    public async Task<IActionResult> Create([FromBody] CreateVehiclePricingDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.FindFirstValue("full_name") ?? User.FindFirstValue(ClaimTypes.Name);
        var userIdGuid = Guid.TryParse(userId, out var parsedId) ? parsedId : (Guid?)null;

        return FromResult(await _mediator.Send(new CreateVehiclePricingCommand(dto, userIdGuid, userName), ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Backoffice")] // Admin only for CRUD operations
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVehiclePricingDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.FindFirstValue("full_name") ?? User.FindFirstValue(ClaimTypes.Name);
        var userIdGuid = Guid.TryParse(userId, out var parsedId) ? parsedId : (Guid?)null;

        return FromResult(await _mediator.Send(new UpdateVehiclePricingCommand(id, dto, userIdGuid, userName), ct));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Backoffice")] // Admin only for CRUD operations
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new DeleteVehiclePricingCommand(id), ct));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = "Backoffice")] // Admin only for CRUD operations
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new ActivateVehiclePricingCommand(id), ct));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "Backoffice")] // Admin only for CRUD operations
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new DeactivateVehiclePricingCommand(id), ct));
}
