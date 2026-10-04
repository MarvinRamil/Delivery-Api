using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Payment.Presentation.Controllers;

/// <summary>
/// Controller for managing saved payment methods.
/// SECURITY: All endpoints require authentication and verify user ownership.
/// </summary>
[Authorize]
[ApiController]
[Route("api/payment-methods")]
public class SavedPaymentMethodsController : BaseController
{
    private readonly IMediator _mediator;

    public SavedPaymentMethodsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Get current user ID from JWT claims.
    /// </summary>
    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return null;
        return userId;
    }

    /// <summary>
    /// Get all saved payment methods for the current user.
    /// GET /api/payment-methods
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        var query = new GetSavedPaymentMethodsQuery(userId.Value);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Get a specific saved payment method by ID.
    /// GET /api/payment-methods/{id}
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        var query = new GetSavedPaymentMethodByIdQuery(id, userId.Value);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Create a new saved payment method.
    /// POST /api/payment-methods
    /// SECURITY: Requires Xendit payment method token (card must be tokenized by Xendit first).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSavedPaymentMethodDto dto, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        // Get user email and full name from claims or user service
        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
        var fullName = User.FindFirstValue("full_name") ?? User.FindFirstValue(ClaimTypes.Name) ?? "Customer";

        if (string.IsNullOrEmpty(email))
            return BadRequest("User email not found");

        var command = new CreateSavedPaymentMethodCommand(
            CustomerId: userId.Value,
            Dto: dto,
            CustomerEmail: email,
            CustomerFullName: fullName
        );

        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Update a saved payment method.
    /// PATCH /api/payment-methods/{id}
    /// SECURITY: Cannot update Xendit tokens or last4Digits (immutable for security).
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSavedPaymentMethodDto dto, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        var command = new UpdateSavedPaymentMethodCommand(id, userId.Value, dto);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Delete a saved payment method.
    /// DELETE /api/payment-methods/{id}
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        var command = new DeleteSavedPaymentMethodCommand(id, userId.Value);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    /// <summary>
    /// Set a saved payment method as default.
    /// POST /api/payment-methods/{id}/set-default
    /// </summary>
    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue)
            return Unauthorized("User ID not found in token");

        var command = new SetDefaultSavedPaymentMethodCommand(id, userId.Value);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }
}
