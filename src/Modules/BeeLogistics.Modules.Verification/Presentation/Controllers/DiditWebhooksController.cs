using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Application.Services;
using BeeLogistics.Modules.Verification.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Presentation.Controllers;

/// <summary>
/// Receives Didit verification webhooks (session status changes).
/// NOTE: External webhooks use HMAC auth (X-Signature + X-Timestamp), not JWT.
/// </summary>
[Route("api/webhooks")]
[ApiController]
public class DiditWebhooksController : ControllerBase
{
    private readonly IKycService _kycService;
    private readonly ICustomerKycService _customerKycService;
    private readonly IOptions<DiditOptions> _options;
    private readonly ILogger<DiditWebhooksController> _logger;

    public DiditWebhooksController(
        IKycService kycService,
        ICustomerKycService customerKycService,
        IOptions<DiditOptions> options,
        ILogger<DiditWebhooksController> logger)
    {
        _kycService = kycService;
        _customerKycService = customerKycService;
        _options = options;
        _logger = logger;
    }

    [HttpPost("didit")]
    [AllowAnonymous]
    public async Task<IActionResult> DiditWebhook(
        [FromHeader(Name = DiditWebhookVerifier.SignatureHeader)] string? signature,
        [FromHeader(Name = DiditWebhookVerifier.TimestampHeader)] string? timestamp,
        CancellationToken ct)
    {
        // Hash the exact bytes as received - never round-trip through a string first. A
        // StreamReader/string round-trip can silently alter bytes (e.g. stripping a UTF-8 BOM),
        // which would make a byte-for-byte-correct signature look like a mismatch.
        if (Request.Body.CanSeek)
            Request.Body.Position = 0;
        using var bodyStream = new MemoryStream();
        await Request.Body.CopyToAsync(bodyStream, ct);
        var rawBodyBytes = bodyStream.ToArray();

        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            _logger.LogWarning("[Didit] [WEBHOOK] Rejected: webhook secret not configured");
            return Unauthorized();
        }

        if (!DiditWebhookVerifier.Verify(rawBodyBytes, signature, timestamp, options.WebhookSecret, options.WebhookToleranceSeconds, out var failureReason))
        {
            _logger.LogWarning("[Didit] [WEBHOOK] Rejected: {Reason}", failureReason);
            return Unauthorized();
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawBodyBytes);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[Didit] [WEBHOOK] Invalid JSON body");
            return BadRequest();
        }

        var sessionId = payload.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
        var status = payload.TryGetProperty("status", out var st) ? st.GetString() : null;
        var vendorData = payload.TryGetProperty("vendor_data", out var vd) && vd.ValueKind == JsonValueKind.String ? vd.GetString() : null;

        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogInformation("[Didit] [WEBHOOK] Ignoring payload without session_id");
            return Ok(new { received = true });
        }

        // Route by the vendor_data tag set at session creation: customer KYC sessions carry the
        // "customer:" prefix and belong to the separate CustomerVerifications table; everything
        // else is a driver session.
        var isCustomer = vendorData?.StartsWith(CustomerKycService.VendorPrefix, StringComparison.Ordinal) == true;
        _logger.LogInformation("[Didit] [WEBHOOK] Processing {Kind} session {SessionId} status {Status}",
            isCustomer ? "customer" : "driver", sessionId, status ?? "(null)");

        try
        {
            if (isCustomer)
                await _customerKycService.ProcessWebhookAsync(sessionId, vendorData, status, ct);
            else
                await _kycService.ProcessWebhookAsync(sessionId, vendorData, status, ct);
            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Didit] [WEBHOOK] Error processing session {SessionId}", sessionId);
            // 5xx so Didit retries (it retries on 5xx/404 with backoff).
            return StatusCode(500);
        }
    }
}
