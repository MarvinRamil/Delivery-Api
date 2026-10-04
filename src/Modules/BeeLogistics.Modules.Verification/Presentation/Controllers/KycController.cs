using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BeeLogistics.Modules.Verification.Presentation.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class KycController : ControllerBase
{
    private readonly IKycService _kycService;
    private readonly IDiditApiClient _didit;
    private readonly ILogger<KycController> _logger;

    public KycController(IKycService kycService, IDiditApiClient didit, ILogger<KycController> logger)
    {
        _kycService = kycService;
        _didit = didit;
        _logger = logger;
    }

    /// <summary>Start a Didit KYC session (ID document + selfie + liveness + face match). Returns the hosted verification URL to open in a WebView.</summary>
    [HttpPost("session")]
    [ProducesResponseType(typeof(CreateKycSessionResult), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(409)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> CreateSession(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        if (!_didit.Enabled)
            return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });

        try
        {
            var result = await _kycService.CreateSessionAsync(userId, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "[KYC] Persisting KYC session failed for user {UserId}", userId);
            return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[KYC] Didit session creation failed for user {UserId}", userId);
            return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });
        }
    }

    /// <summary>Current KYC status for the authenticated driver (NotStarted, Pending, InProgress, InReview, Approved, Declined, Abandoned, Expired, Legacy).</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(KycStatusResult), 200)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _kycService.GetStatusAsync(userId, cancellationToken);
        return Ok(result);
    }
}
