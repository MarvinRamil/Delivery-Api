using BeeLogistics.Modules.Rating.Application.DTOs;
using BeeLogistics.Modules.Rating.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Rating.Presentation.Controllers;

[Route("api/[controller]")]
[ApiController]
public class RatingsController : BaseController
{
    private readonly IMediator _mediator;

    public RatingsController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetRatingByIdQuery(id), ct));

    [HttpGet("booking/{bookingId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByBookingId(Guid bookingId, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetRatingByBookingIdQuery(bookingId), ct));

    [HttpGet("driver/{driverId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByDriverId(Guid driverId, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetRatingsByDriverIdQuery(driverId), ct));

    [HttpGet("customer/{customerId:guid}")]
    [Authorize]
    public async Task<IActionResult> GetByCustomerId(Guid customerId, CancellationToken ct)
    {
        // Verify user can only access their own ratings
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != customerId.ToString())
            return Forbid();

        return FromResult(await _mediator.Send(new GetRatingsByCustomerIdQuery(customerId), ct));
    }

    [HttpGet("driver/{driverId:guid}/summary")]
    [AllowAnonymous]
    public async Task<IActionResult> GetDriverRatingSummary(Guid driverId, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetDriverRatingQuery(driverId), ct));

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateRatingDto dto, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var customerId))
            return Unauthorized();

        return FromResult(await _mediator.Send(new CreateRatingCommand(
            dto.BookingId,
            dto.DriverId,
            customerId,
            dto.Stars,
            dto.Comment,
            dto.Category
        ), ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRatingDto dto, CancellationToken ct)
    {
        // Verify user owns this rating
        var ratingResult = await _mediator.Send(new GetRatingByIdQuery(id), ct);
        if (!ratingResult.IsSuccess || ratingResult.Value == null)
            return FromResult(ratingResult);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != ratingResult.Value.CustomerId.ToString())
            return Forbid();

        return FromResult(await _mediator.Send(new UpdateRatingCommand(id, dto.Comment), ct));
    }
}
