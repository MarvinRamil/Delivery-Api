using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

/// <summary>
/// Receives Clerk webhooks and keeps the local user record in sync (background
/// sync — sign-in itself no longer depends on this; /api/auth/me provisions the
/// user just-in-time if the webhook hasn't arrived). Verified via the Svix
/// signature scheme (HMAC-SHA256), not JWT, hence [AllowAnonymous].
///
/// user.created / user.updated → upsert + link ClerkUserId, sync email/phone/name.
/// user.deleted → soft-delete. The local record stays authoritative for role.
/// All create/link logic lives in <see cref="ClerkUserProvisioningService"/>,
/// shared with the just-in-time path.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/clerk")]
public class ClerkWebhookController : ControllerBase
{
    private readonly ClerkSettings _settings;
    private readonly ClerkUserProvisioningService _provisioning;
    private readonly ILogger<ClerkWebhookController> _logger;

    public ClerkWebhookController(
        IOptions<ClerkSettings> settings,
        ClerkUserProvisioningService provisioning,
        ILogger<ClerkWebhookController> logger)
    {
        _settings = settings.Value;
        _provisioning = provisioning;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.WebhookSigningSecret))
        {
            _logger.LogWarning("[ClerkWebhook] Received webhook but WebhookSigningSecret is not configured.");
            return StatusCode(503, "Clerk webhooks not configured.");
        }

        string body;
        Request.EnableBuffering();
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(ct);
            Request.Body.Position = 0;
        }

        if (!VerifySvixSignature(body))
        {
            _logger.LogWarning("[ClerkWebhook] Signature verification failed.");
            return Unauthorized();
        }

        string? type;
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!root.TryGetProperty("data", out data) || string.IsNullOrEmpty(type))
            {
                return Ok(); // Nothing we model.
            }
            // Clone so the JsonElement stays valid after the document is disposed.
            data = data.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClerkWebhook] Failed to parse payload.");
            return BadRequest("Invalid payload.");
        }

        switch (type)
        {
            case "user.created":
            case "user.updated":
                {
                    var userData = ClerkUserProvisioningService.ParseClerkUser(data);
                    if (userData is not null)
                    {
                        // Background sync only: link/update existing users. Brand-new
                        // users are created by the role-aware /api/auth/me path so the
                        // correct role is always applied (allowCreate: false here).
                        await _provisioning.UpsertAsync(userData, allowCreate: false, ct: ct);
                    }
                    break;
                }
            case "user.deleted":
                {
                    var id = data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                    {
                        await _provisioning.SoftDeleteAsync(id, ct);
                    }
                    break;
                }
            default:
                _logger.LogDebug("[ClerkWebhook] Ignoring event type {Type}", type);
                break;
        }

        return Ok();
    }

    // --- Svix signature verification (HMAC-SHA256) ---
    private bool VerifySvixSignature(string payload)
    {
        var svixId = Request.Headers["svix-id"].FirstOrDefault();
        var svixTimestamp = Request.Headers["svix-timestamp"].FirstOrDefault();
        var svixSignature = Request.Headers["svix-signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(svixId) || string.IsNullOrEmpty(svixTimestamp) || string.IsNullOrEmpty(svixSignature))
        {
            return false;
        }

        var secret = _settings.WebhookSigningSecret;
        var keyPart = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyPart);
        }
        catch (FormatException)
        {
            _logger.LogError("[ClerkWebhook] Signing secret is not valid base64.");
            return false;
        }

        var signedContent = $"{svixId}.{svixTimestamp}.{payload}";
        using var hmac = new HMACSHA256(keyBytes);
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedContent));

        // Header is space-separated "v1,<base64sig>" entries; any match passes.
        foreach (var part in svixSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var commaIdx = part.IndexOf(',');
            var sigB64 = commaIdx >= 0 ? part[(commaIdx + 1)..] : part;
            byte[] provided;
            try
            {
                provided = Convert.FromBase64String(sigB64);
            }
            catch (FormatException)
            {
                continue;
            }
            if (CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                return true;
            }
        }
        return false;
    }
}
