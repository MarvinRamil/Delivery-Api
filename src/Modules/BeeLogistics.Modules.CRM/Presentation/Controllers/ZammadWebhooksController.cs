using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.CRM.Application.Handlers;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.CRM.Presentation.Controllers;

/// <summary>
/// Receives webhooks from Zammad when tickets are updated.
/// NOTE: Inherits from ControllerBase (not BaseController) — external webhooks use HMAC auth, not JWT.
/// </summary>
[Route("api/webhooks")]
[ApiController]
public class ZammadWebhooksController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IOptions<Infrastructure.ZammadSettings> _zammadSettings;
    private readonly ILogger<ZammadWebhooksController> _logger;

    public ZammadWebhooksController(
        IMediator mediator,
        IOptions<Infrastructure.ZammadSettings> zammadSettings,
        ILogger<ZammadWebhooksController> logger)
    {
        _mediator = mediator;
        _zammadSettings = zammadSettings;
        _logger = logger;
    }

    [HttpPost("zammad")]
    public async Task<IActionResult> ZammadWebhook(
        [FromHeader(Name = "X-Hub-Signature")] string? hubSignature,
        CancellationToken ct)
    {
        if (Request.Body.CanSeek)
            Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);
        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        var webhookSecret = _zammadSettings.Value.WebhookSecret;
        if (!string.IsNullOrWhiteSpace(webhookSecret))
        {
            if (string.IsNullOrWhiteSpace(hubSignature))
            {
                _logger.LogWarning("[ZAMMAD] [WEBHOOK] Rejected: X-Hub-Signature header missing");
                return Unauthorized();
            }

            if (!VerifyHmacSignature(rawBody, hubSignature, webhookSecret))
            {
                _logger.LogWarning("[ZAMMAD] [WEBHOOK] Rejected: HMAC signature verification failed");
                return Unauthorized();
            }
        }

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[ZAMMAD] [WEBHOOK] Invalid JSON body");
            return BadRequest();
        }

        if (!payload.TryGetProperty("ticket", out var ticketProp) || ticketProp.ValueKind != JsonValueKind.Object)
        {
            _logger.LogInformation("[ZAMMAD] [WEBHOOK] Ignoring payload without ticket object");
            return Ok(new { received = true });
        }

        var ticketObj = ticketProp;
        if (!ticketObj.TryGetProperty("id", out var idProp))
        {
            _logger.LogInformation("[ZAMMAD] [WEBHOOK] Ignoring payload without ticket.id");
            return Ok(new { received = true });
        }

        var zammadTicketId = idProp.GetInt32();
        var state = ticketObj.TryGetProperty("state", out var stateProp)
            ? stateProp.GetString()?.ToLowerInvariant()
            : null;

        string? ownerName = null;
        if (ticketObj.TryGetProperty("owner", out var ownerProp) && ownerProp.ValueKind == JsonValueKind.Object)
        {
            var first = ownerProp.TryGetProperty("firstname", out var fn) ? fn.GetString() : null;
            var last = ownerProp.TryGetProperty("lastname", out var ln) ? ln.GetString() : null;
            if (!string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(last))
                ownerName = $"{first ?? ""} {last ?? ""}".Trim();
        }

        // Zammad sends the article that triggered the event next to the ticket. It is absent on a
        // pure state or owner change, so everything here is optional - but when it is present it
        // is the only place an agent's reply appears. Reading just the ticket, as this used to,
        // meant the one event a waiting driver cares about was the one we discarded.
        var article = ParseArticle(payload);

        _logger.LogInformation(
            "[ZAMMAD] [WEBHOOK] Processing - ZammadId={ZammadId}, State={State}, ArticleId={ArticleId}",
            zammadTicketId, state ?? "(null)", article?.Id.ToString() ?? "(none)");

        try
        {
            await _mediator.Send(new ProcessZammadWebhookCommand(zammadTicketId, state, ownerName, article), ct);
            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ZAMMAD] [WEBHOOK] Error processing webhook for ZammadId={ZammadId}", zammadTicketId);
            return Ok(new { received = true }); // Always 200 to avoid Zammad retries
        }
    }

    /// <summary>
    /// Reads the "article" object from a webhook payload, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Anything malformed yields null rather than throwing: a webhook whose article we cannot
    /// read should still deliver its state and owner changes. The id and body are the only
    /// required parts - an article we cannot identify cannot be de-duplicated, and one with no
    /// text is nothing to show.
    /// </remarks>
    private static ZammadWebhookArticle? ParseArticle(JsonElement payload)
    {
        if (!payload.TryGetProperty("article", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;

        if (!a.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var articleId))
            return null;

        var body = a.TryGetProperty("body", out var b) ? b.GetString() : null;
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var contentType = a.TryGetProperty("content_type", out var ct) ? ct.GetString() : null;
        var sender = a.TryGetProperty("sender", out var s) ? s.GetString() : null;
        var from = a.TryGetProperty("from", out var f) ? f.GetString() : null;

        // Zammad renders "internal" as a real boolean, but a customised payload can stringify it.
        var isInternal = a.TryGetProperty("internal", out var i)
            && (i.ValueKind == JsonValueKind.True
                || (i.ValueKind == JsonValueKind.String && bool.TryParse(i.GetString(), out var parsed) && parsed));

        DateTime? createdAt = a.TryGetProperty("created_at", out var c)
            && DateTime.TryParse(c.GetString(), out var dt)
                ? dt
                : null;

        return new ZammadWebhookArticle(articleId, body, contentType, sender, isInternal, from, createdAt);
    }

    private static bool VerifyHmacSignature(string rawBody, string hubSignature, string secret)
    {
        const string prefix = "sha1=";
        if (!hubSignature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var expectedHex = hubSignature.AsSpan(prefix.Length).Trim().ToString();
        if (expectedHex.Length != 40)
            return false;

        var payloadBytes = Encoding.UTF8.GetBytes(rawBody);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA1.HashData(secretBytes, payloadBytes);
        var computedHex = Convert.ToHexString(hash).ToLowerInvariant();

        return string.Equals(expectedHex, computedHex, StringComparison.OrdinalIgnoreCase);
    }
}
