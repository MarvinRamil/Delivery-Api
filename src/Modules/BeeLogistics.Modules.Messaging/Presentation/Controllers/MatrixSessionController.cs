using System.Security.Claims;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Application.DTOs;
using BeeLogistics.Modules.Messaging.Application.Services;
using BeeLogistics.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Messaging.Presentation.Controllers;

/// <summary>
/// Issues a caller their own Matrix credentials. Called by the driver and customer apps.
/// </summary>
[ApiController]
[Authorize]
[Route("api/matrix")]
public class MatrixSessionController : ControllerBase
{
    private static readonly string[] AllowedPlatforms = ["driver-android", "driver-ios", "web", "backoffice"];

    private readonly IMatrixSessionIssuer _issuer;
    private readonly MatrixOptions _options;
    private readonly ILogger<MatrixSessionController> _logger;

    public MatrixSessionController(
        IMatrixSessionIssuer issuer,
        IOptions<MatrixOptions> options,
        ILogger<MatrixSessionController> logger)
    {
        _issuer = issuer;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Mints a Matrix access token for the authenticated caller.
    /// </summary>
    /// <remarks>
    /// The caller is taken from the JWT, never from the request body — accepting a user id here
    /// would let any authenticated user mint credentials for anyone else and read their chats.
    /// </remarks>
    [HttpPost("session")]
    public async Task<IActionResult> CreateSession([FromBody] CreateMatrixSessionRequest? request, CancellationToken ct)
    {
        if (!_options.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse.Fail("Chat is not enabled."));

        var beeUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(beeUserId))
            return Unauthorized(ApiResponse.Fail("No user id on this token."));

        var platform = request?.Platform?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(platform)) platform = "web";

        // Bounded because it is half of a unique index and appears in a device display name; an
        // arbitrary client string would let a caller create unlimited devices for themselves.
        if (!AllowedPlatforms.Contains(platform))
            return BadRequest(ApiResponse.Fail($"Unknown platform. Expected one of: {string.Join(", ", AllowedPlatforms)}."));

        var displayName = User.FindFirstValue("full_name") ?? User.FindFirstValue(ClaimTypes.Name);

        var session = await _issuer.IssueAsync(beeUserId, displayName, platform, ct);

        _logger.LogInformation("Issued a Matrix session to {MatrixUserId} for {Platform}", session.UserId, platform);

        return Ok(ApiResponse<MatrixSessionDto>.Ok(session));
    }
}

public sealed record CreateMatrixSessionRequest
{
    /// <summary>Which client this is. One device is kept per (user, platform).</summary>
    public string? Platform { get; init; }
}
