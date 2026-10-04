using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

/// <summary>
/// Booking queries and lifecycle. Creation lives in <see cref="BookingCreationController"/>,
/// multipart uploads in <see cref="BookingUploadsController"/>, and image serving in
/// <see cref="BookingImagesController"/> — all three still under /api/bookings.
/// </summary>
public class BookingsController : BaseController
{
    private readonly IMediator _mediator;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuthorizationService _authorizationService;

    public BookingsController(
        IMediator mediator,
        UserManager<ApplicationUser> userManager,
        IAuthorizationService authorizationService)
    {
        _mediator = mediator;
        _userManager = userManager;
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// The caller's ASP.NET Identity user id, or null when the token carries no usable subject.
    /// </summary>
    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    /// <summary>
    /// Whether the caller satisfies the "Backoffice" policy. Evaluated through
    /// IAuthorizationService so the policy stays defined in exactly one place (Program.cs)
    /// rather than being re-implemented from raw claims here.
    /// </summary>
    private async Task<bool> IsElevatedAsync()
        => (await _authorizationService.AuthorizeAsync(User, "Backoffice")).Succeeded;

    [HttpGet]
    [Authorize(Roles = "SuperAdmin,Admin,Owner,Dispatcher")]
    public async Task<IActionResult> GetAll(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        return FromResult(await _mediator.Send(new GetBookingsQuery(page, pageSize), ct));
    }

    // Specific routes must come BEFORE generic {id:guid} route to avoid route conflicts
    [HttpGet("my-bookings")]
    [Authorize]
    public async Task<IActionResult> GetMyBookings(CancellationToken ct)
    {
        // Get user ID from token and fetch user from database
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userId))
            return Unauthorized("User ID not found in token");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null || string.IsNullOrEmpty(user.Email))
            return Unauthorized("User not found");

        return FromResult(await _mediator.Send(new GetMyBookingsQuery(user.Email), ct));
    }

    [HttpGet("driver/{driverId:guid}")]
    [Authorize]
    public async Task<IActionResult> GetByDriver(Guid driverId, CancellationToken ct)
    {
        // Verify the driver ID matches the authenticated user (drivers can only see their own bookings)
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var authenticatedUserId))
            return Unauthorized("User ID not found in token");

        // Drivers can only access their own bookings
        if (driverId != authenticatedUserId)
            return Forbid("You can only access your own bookings");

        return FromResult(await _mediator.Send(new GetBookingsByDriverQuery(driverId), ct));
    }

    [HttpGet("customer/{customerId:guid}")]
    public async Task<IActionResult> GetByCustomer(Guid customerId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var authenticatedUserId))
            return Unauthorized("User ID not found in token");

        var userRole = User.FindFirst("role")?.Value ?? User.FindFirstValue(ClaimTypes.Role);
        var isElevatedRole = userRole == "SuperAdmin" || userRole == "Admin" || userRole == "Owner" || userRole == "Dispatcher";

        // Prevent cross-customer data access unless caller has elevated operational role.
        if (!isElevatedRole && authenticatedUserId != customerId)
            return Forbid("You can only access your own bookings");

        return FromResult(await _mediator.Send(new GetBookingsByCustomerQuery(customerId), ct));
    }

    [HttpGet("{id:guid}")]
    [Authorize] // Owning customer, assigned driver or backoffice - enforced by IBookingAccessPolicy in the handler
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null)
            return Unauthorized("User ID not found in token");

        return FromResult(await _mediator.Send(new GetBookingByIdQuery(id, callerUserId.Value, await IsElevatedAsync()), ct));
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize] // Owning customer, assigned driver or backoffice - enforced by IBookingAccessPolicy in the handler
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateBookingStatusDto dto, CancellationToken ct)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null)
            return Unauthorized("User ID not found in token");

        return FromResult(await _mediator.Send(new UpdateBookingStatusCommand(id, dto.Status, callerUserId.Value, await IsElevatedAsync()), ct));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize] // Both customers and drivers can cancel bookings
    public async Task<IActionResult> CancelBooking(Guid id, [FromBody] CancelBookingDto dto, CancellationToken ct)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null)
            return Unauthorized("User ID not found in token");

        // Convert enum reason to string with custom reason if needed
        var reasonText = GetCancellationReasonText(dto.Reason, dto.CustomReason);

        return FromResult(await _mediator.Send(new CancelBookingCommand(id, reasonText, callerUserId.Value, await IsElevatedAsync()), ct));
    }

    private string GetCancellationReasonText(CancellationReason reason, string? customReason)
    {
        var reasonText = reason switch
        {
            CancellationReason.CustomerRequest => "Customer requested cancellation",
            CancellationReason.DriverUnavailable => "Driver unavailable",
            CancellationReason.NoDriverFound => "No driver found",
            CancellationReason.PickupLocationInaccessible => "Pickup location inaccessible",
            CancellationReason.DeliveryLocationInaccessible => "Delivery location inaccessible",
            CancellationReason.ItemNotReady => "Item not ready for pickup",
            CancellationReason.WeatherConditions => "Weather conditions",
            CancellationReason.VehicleBreakdown => "Vehicle breakdown",
            CancellationReason.Emergency => "Emergency situation",
            CancellationReason.Other => !string.IsNullOrWhiteSpace(customReason) ? $"Other: {customReason}" : "Other",
            _ => "Unknown reason"
        };
        return reasonText;
    }

    // Backoffice only: deleting a booking is an operations action. Customers cancel instead
    // (POST /{id}/cancel), which records a reason and leaves an audit trail.
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null)
            return Unauthorized("User ID not found in token");

        return FromResult(await _mediator.Send(new DeleteBookingCommand(id, callerUserId.Value, await IsElevatedAsync()), ct));
    }

    // New endpoints for BEE assignment system - SuperAdmin/Admin (backoffice) only
    [HttpGet("pending-assignment")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> GetPendingAssignment(CancellationToken ct)
        => FromResult(await _mediator.Send(new GetPendingAssignmentBookingsQuery(), ct));

    [HttpPost("{id:guid}/classify-size")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> ClassifySize(Guid id, [FromBody] ClassifyBookingSizeDto dto, CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        return FromResult(await _mediator.Send(new ClassifyBookingSizeCommand(id, dto.Size, userId), ct));
    }

    [HttpPost("{id:guid}/start-broadcast")]
    [Authorize(Policy = "Backoffice")]
    public async Task<IActionResult> StartBroadcast(Guid id, CancellationToken ct)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized("User ID not found in token");

        return FromResult(await _mediator.Send(new StartBroadcastingBookingCommand(id, userId), ct));
    }

    // Endpoints for the multi-stop, driver-broadcast model
    [HttpPost("calculate-fare")]
    [Authorize]
    public async Task<IActionResult> CalculateFare(
        [FromBody] CalculateFareQuery query,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpGet("{id:guid}/available-drivers")]
    [Authorize]
    public async Task<IActionResult> GetAvailableDrivers(
        Guid id,
        CancellationToken ct = default)
    {
        var query = new GetAvailableDriversQuery(id);
        var result = await _mediator.Send(query, ct);
        return FromResult(result);
    }

    [HttpPost("{id:guid}/select-driver")]
    [Authorize]
    public async Task<IActionResult> SelectDriver(
        Guid id,
        [FromBody] SelectDriverRequest request,
        CancellationToken ct = default)
    {
        var customerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());
        
        var command = new SelectDriverCommand(id, request.DriverId, customerId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpPost("{id:guid}/stops/{stopId:guid}/complete")]
    [Authorize(Roles = "Driver")]
    public async Task<IActionResult> CompleteStop(
        Guid id,
        Guid stopId,
        CancellationToken ct = default)
    {
        var driverId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());
        
        var command = new CompleteStopCommand(id, stopId, driverId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpPost("{id:guid}/stops/{stopId:guid}/in-transit")]
    [Authorize(Roles = "Driver")]
    public async Task<IActionResult> InTransitStop(
        Guid id,
        Guid stopId,
        CancellationToken ct = default)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var driverId))
            return BadRequest(new { success = false, message = "Invalid driver ID in token" });

        var command = new InTransitStopCommand(id, stopId, driverId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    [HttpPost("{id:guid}/stops/{stopId:guid}/arrive")]
    [Authorize(Roles = "Driver")]
    public async Task<IActionResult> ArriveStop(
        Guid id,
        Guid stopId,
        CancellationToken ct = default)
    {
        var driverId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.Empty.ToString());

        var command = new ArriveStopCommand(id, stopId, driverId);
        var result = await _mediator.Send(command, ct);
        return FromResult(result);
    }

    public record SelectDriverRequest(Guid DriverId);
}
