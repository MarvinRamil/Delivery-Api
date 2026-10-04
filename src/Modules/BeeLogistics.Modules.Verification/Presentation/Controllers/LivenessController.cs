using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Verification.Presentation.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LivenessController : ControllerBase
{
    private readonly ILivenessSessionService _sessionService;
    private readonly ILogger<LivenessController> _logger;

    public LivenessController(ILivenessSessionService sessionService, ILogger<LivenessController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>Start a liveness check session. Returns a session ID and randomized directions (e.g. left, right, up, down).</summary>
    [HttpPost("session")]
    [ProducesResponseType(typeof(Application.DTOs.CreateLivenessSessionResult), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> CreateSession(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _sessionService.CreateSessionAsync(userId, cancellationToken);
        _logger.LogInformation("[Liveness] Session created for user {UserId}, session {SessionId}, directions: {Directions}",
            userId, result.SessionId, string.Join(", ", result.Directions));
        return Ok(result);
    }

    /// <summary>Submit an image for one direction. Direction must be one of: left, right, up, down (as returned by CreateSession).</summary>
    [HttpPost("session/{sessionId}/submit")]
    [ProducesResponseType(typeof(Application.DTOs.SubmitLivenessImageResult), 200)]
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

        var dir = direction.Trim();
        await using var stream = image.OpenReadStream();
        var result = await _sessionService.SubmitImageAsync(sessionId, dir, stream, cancellationToken);
        _logger.LogInformation("[Liveness] Submit image session {SessionId} direction {Direction} user {UserId}: directionPassed={DirectionPassed}, allPassed={AllPassed}, error={Error}",
            sessionId, dir, userId, result.DirectionPassed, result.AllPassed, result.Error ?? "");
        return Ok(result);
    }

    /// <summary>Get current session status (InProgress, Passed, Failed, Expired, NotFound).</summary>
    [HttpGet("session/{sessionId}")]
    [ProducesResponseType(typeof(Application.DTOs.LivenessSessionStatusResult), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> GetStatus(string sessionId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _sessionService.GetStatusAsync(sessionId, cancellationToken);
        _logger.LogInformation("[Liveness] Status session {SessionId} user {UserId}: status={Status}", sessionId, userId, result.Status);
        return Ok(result);
    }
}
