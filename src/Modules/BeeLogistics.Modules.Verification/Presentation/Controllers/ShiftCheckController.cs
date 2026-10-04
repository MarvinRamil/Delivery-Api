using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Verification.Presentation.Controllers;

/// <summary>
/// Per-shift face check: same head-pose challenge as onboarding liveness, but each
/// frame is also face-matched against the driver's KYC reference selfie.
/// </summary>
[ApiController]
[Route("api/shift-check")]
[Authorize]
public class ShiftCheckController : ControllerBase
{
    private readonly IShiftCheckService _shiftCheckService;
    private readonly ILogger<ShiftCheckController> _logger;

    public ShiftCheckController(IShiftCheckService shiftCheckService, ILogger<ShiftCheckController> logger)
    {
        _shiftCheckService = shiftCheckService;
        _logger = logger;
    }

    /// <summary>Start a shift face-check session. Returns a session ID and randomized directions.</summary>
    [HttpPost("session")]
    [ProducesResponseType(typeof(CreateShiftCheckSessionResult), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> CreateSession(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        try
        {
            var result = await _shiftCheckService.CreateSessionAsync(userId, cancellationToken);
            _logger.LogInformation("[ShiftCheck] Session created for user {UserId}, session {SessionId}", userId, result.SessionId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = "NO_REFERENCE", error = ex.Message });
        }
    }

    /// <summary>Submit an image for one direction (left, right, up, down as returned by CreateSession).</summary>
    [HttpPost("session/{sessionId}/submit")]
    [ProducesResponseType(typeof(SubmitShiftCheckImageResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [RequestSizeLimit(10_485_760)] // 10 MB
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> SubmitImage(string sessionId, [FromForm] string direction, IFormFile image, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(direction) || image == null || image.Length == 0)
            return BadRequest(new { error = "direction and image are required." });

        await using var stream = image.OpenReadStream();
        var result = await _shiftCheckService.SubmitImageAsync(sessionId, userId, direction.Trim(), stream, cancellationToken);
        return Ok(result);
    }

    /// <summary>Get current session status (InProgress, Passed, Expired, NotFound).</summary>
    [HttpGet("session/{sessionId}")]
    [ProducesResponseType(typeof(LivenessSessionStatusResult), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> GetStatus(string sessionId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _shiftCheckService.GetStatusAsync(sessionId, cancellationToken);
        return Ok(result);
    }
}
