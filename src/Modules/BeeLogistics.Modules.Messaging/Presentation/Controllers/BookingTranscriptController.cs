using System.Security.Claims;
using BeeLogistics.Modules.Messaging.Application.DTOs;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Messaging.Presentation.Controllers;

/// <summary>
/// Lets back-office staff read a booking's conversation.
/// </summary>
/// <remarks>
/// <para>
/// Reaches bee-backend unchanged through <c>backoffice-backend</c>'s <c>PassthroughController</c>,
/// which forwards unmatched <c>/api/*</c> with S2S credentials. That handler mints
/// <c>SuperAdmin</c> + <c>is_backoffice</c> claims plus <c>acting_admin</c> (the admin's email), so
/// <c>BackofficeAuthorizationMiddleware</c> gates it and we still know which human asked.
/// </para>
/// <para>
/// Served from the archive rather than Synapse, so nobody joins the room to read it and the two
/// parties never gain a silent third member.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Roles = "SuperAdmin,Admin")]
[Route("api/matrix/bookings")]
public class BookingTranscriptController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly IBookingTranscriptReader _reader;
    private readonly ILogger<BookingTranscriptController> _logger;

    public BookingTranscriptController(IBookingTranscriptReader reader, ILogger<BookingTranscriptController> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    [HttpGet("{bookingId:guid}/messages")]
    public async Task<IActionResult> GetTranscript(Guid bookingId, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        if (skip < 0) skip = 0;
        // Capped rather than honoured: an uncapped take on a chat archive is an easy way to pull
        // an entire table over the wire.
        take = Math.Clamp(take, 1, MaxPageSize);

        var transcript = await _reader.ReadAsync(bookingId, skip, take, ct);
        if (transcript is null)
            return NotFound(ApiResponse.Fail("This booking has no chat room."));

        // Reading a private conversation between two people is exactly the kind of access that
        // should leave a trace. acting_admin survives the S2S hop for this reason.
        _logger.LogInformation(
            "Booking transcript {BookingId} read by {ActingAdmin}",
            bookingId, User.FindFirstValue("acting_admin") ?? User.FindFirstValue(ClaimTypes.Name) ?? "unknown");

        return Ok(ApiResponse<BookingTranscriptDto>.Ok(transcript));
    }
}
