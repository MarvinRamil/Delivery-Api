using BeeLogistics.Api.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Api.Controllers;

[Route("api/dashboard")]
public class DashboardController : BaseController
{
    private readonly IMediator _mediator;

    public DashboardController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// Get dashboard statistics (SuperAdmin only - backoffice)
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> GetStats(
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        // Simplified: No company filtering needed - SuperAdmin sees all data
        return FromResult(await _mediator.Send(new GetDashboardStatsQuery(dateFrom, dateTo), ct));
    }

    /// <summary>
    /// Get customer-specific dashboard statistics
    /// </summary>
    [HttpGet("customer/stats")]
    [Authorize(Roles = "Customer")]
    public async Task<IActionResult> GetCustomerStats(
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        // Get customer ID from claims
        var customerIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerIdClaim) || !Guid.TryParse(customerIdClaim, out var customerId))
        {
            return Unauthorized();
        }
        
        return FromResult(await _mediator.Send(new GetCustomerDashboardStatsQuery(customerId, dateFrom, dateTo), ct));
    }
}

