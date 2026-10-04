using BeeLogistics.Modules.Drivers.Application.DTOs;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Drivers.Presentation.Controllers;

/// <summary>
/// Controller for managing saved withdrawal methods (bank accounts).
/// SECURITY: All endpoints require authentication and verify driver ownership.
/// </summary>
[Authorize]
[ApiController]
[Route("api/drivers/{driverId:guid}/withdrawal-methods")]
public class SavedWithdrawalMethodsController : BaseController
{
    private readonly IMediator _mediator;

    public SavedWithdrawalMethodsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Verify that the current user is the driver or has admin access.
    /// </summary>
    private IActionResult? EnsureDriverAccess(Guid driverId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var currentUserId))
            return Unauthorized("User ID not found in token");

        // Check if user is accessing their own data or is admin
        var isAdmin = User.IsInRole("SuperAdmin") || User.IsInRole("Admin");
        var isSelf = currentUserId == driverId;

        if (!isAdmin && !isSelf)
            return Forbid("You can only access your own withdrawal methods");

        return null;
    }

    /// <summary>
    /// Get all saved withdrawal methods for a driver.
    /// GET /api/drivers/{driverId}/withdrawal-methods
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(Guid driverId, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetSavedWithdrawalMethodsQuery(driverId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Get a specific saved withdrawal method by ID.
    /// GET /api/drivers/{driverId}/withdrawal-methods/{id}
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid driverId, Guid id, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var query = new GetSavedWithdrawalMethodByIdQuery(id, driverId);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Create a new saved withdrawal method.
    /// POST /api/drivers/{driverId}/withdrawal-methods
    /// SECURITY: Account number will be encrypted at rest.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(Guid driverId, [FromBody] CreateSavedWithdrawalMethodDto dto, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new CreateSavedWithdrawalMethodCommand(driverId, dto);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Update a saved withdrawal method.
    /// PATCH /api/drivers/{driverId}/withdrawal-methods/{id}
    /// SECURITY: Account number will be encrypted at rest.
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid driverId, Guid id, [FromBody] UpdateSavedWithdrawalMethodDto dto, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new UpdateSavedWithdrawalMethodCommand(id, driverId, dto);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Delete a saved withdrawal method.
    /// DELETE /api/drivers/{driverId}/withdrawal-methods/{id}
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid driverId, Guid id, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new DeleteSavedWithdrawalMethodCommand(id, driverId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Set a saved withdrawal method as default.
    /// POST /api/drivers/{driverId}/withdrawal-methods/{id}/set-default
    /// </summary>
    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid driverId, Guid id, CancellationToken ct = default)
    {
        var accessDenied = EnsureDriverAccess(driverId);
        if (accessDenied != null) return accessDenied;

        var command = new SetDefaultSavedWithdrawalMethodCommand(id, driverId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }
}
