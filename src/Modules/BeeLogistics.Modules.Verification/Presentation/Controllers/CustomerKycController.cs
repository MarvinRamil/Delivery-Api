using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BeeLogistics.Modules.Verification.Presentation.Controllers;

/// <summary>
/// Customer identity verification (Didit KYC). Separate from the driver KycController and
/// backed by the CustomerVerifications table.
/// </summary>
[ApiController]
[Route("api/customer/kyc")]
[Authorize]
public class CustomerKycController : ControllerBase
{
    private readonly ICustomerKycService _kycService;
    private readonly IDiditApiClient _didit;
    private readonly ILogger<CustomerKycController> _logger;

    public CustomerKycController(ICustomerKycService kycService, IDiditApiClient didit, ILogger<CustomerKycController> logger)
    {
        _kycService = kycService;
        _didit = didit;
        _logger = logger;
    }

    /// <summary>Start a Didit KYC session for the customer. Returns the hosted verification URL to open in a WebView.</summary>
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
            _logger.LogError(ex, "[CustomerKYC] Persisting KYC session failed for customer {UserId}", userId);
            return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[CustomerKYC] Didit session creation failed for customer {UserId}", userId);
            return StatusCode(503, new { error = "Identity verification is temporarily unavailable." });
        }
    }

    /// <summary>Current KYC status for the authenticated customer.</summary>
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
