using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Modules.Identity.Domain;
using MediatR;
using Microsoft.AspNetCore.Identity;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace BeeLogistics.Modules.Bookings.Presentation.Images;

/// <summary>
/// Who may read a booking's images: backoffice roles, or the customer the booking belongs to.
/// </summary>
/// <remarks>
/// Extracted verbatim from BookingsController so the image-serving and image-upload endpoints
/// can live in different controllers and still answer the question identically.
///
/// This is NOT the same rule as <c>IBookingAccessPolicy</c> in Application/Services, which
/// works from a Booking plus a caller id and an elevated flag. This one reads the role claim
/// directly and then falls back to matching the caller's e-mail against their own bookings.
/// The two disagree; unifying them would change who can see images, so it is deliberately not
/// done here.
/// </remarks>
internal sealed class BookingImageAccessPolicy
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IMediator _mediator;

    public BookingImageAccessPolicy(UserManager<ApplicationUser> userManager, IMediator mediator)
    {
        _userManager = userManager;
        _mediator = mediator;
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal caller, Guid bookingId, CancellationToken ct)
    {
        var userId = caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var userRole = caller.FindFirst("role")?.Value ?? caller.FindFirstValue(ClaimTypes.Role);
        if (userRole == "SuperAdmin" || userRole == "Admin" || userRole == "Owner" || userRole == "Dispatcher")
            return true;
        if (userId == null) return false;
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null || string.IsNullOrEmpty(user.Email)) return false;
        var myBookingsResult = await _mediator.Send(new GetMyBookingsQuery(user.Email), ct);
        return myBookingsResult.IsSuccess && myBookingsResult.Value != null && myBookingsResult.Value.Any(b => b.Id == bookingId);
    }
}
