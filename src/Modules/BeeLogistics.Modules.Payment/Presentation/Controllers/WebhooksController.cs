using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Presentation.Webhooks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;

namespace BeeLogistics.Modules.Payment.Presentation.Controllers;

/// <summary>
/// Handles external payment provider webhooks.
/// NOTE: Inherits from ControllerBase (not BaseController) because:
/// - External webhooks use callback token authentication, not JWT
/// - Must be publicly accessible without [Authorize] attribute
/// </summary>
[Route("api/webhooks")]
[ApiController]
public class WebhooksController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly XenditOptions _xenditOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<WebhooksController> _logger;
    private readonly IPaymentOutboxPublisher _outboxPublisher;
    private readonly WebhookEventStore _eventStore;
    private readonly IEnumerable<IPaymentWebhookPostProcessor> _webhookPostProcessors;
    private readonly IDisbursementWebhookProcessor? _disbursementProcessor;

    public WebhooksController(
        IMediator mediator,
        IOptions<XenditOptions> xenditOptions,
        IHostEnvironment environment,
        ILogger<WebhooksController> logger,
        IPaymentOutboxPublisher outboxPublisher,
        WebhookEventStore eventStore,
        IEnumerable<IPaymentWebhookPostProcessor> webhookPostProcessors,
        IDisbursementWebhookProcessor? disbursementProcessor = null)
    {
        _mediator = mediator;
        _xenditOptions = xenditOptions.Value;
        _environment = environment;
        _logger = logger;
        _outboxPublisher = outboxPublisher;
        _eventStore = eventStore;
        _webhookPostProcessors = webhookPostProcessors ?? Array.Empty<IPaymentWebhookPostProcessor>();
        _disbursementProcessor = disbursementProcessor;
    }

    [HttpPost("xendit")]
    public async Task<IActionResult> XenditWebhook(
        [FromHeader(Name = "x-callback-token")] string? callbackToken,
        [FromHeader(Name = "x-xendit-signature")] string? xenditSignature,
        CancellationToken ct)
    {
        // Read raw body first (middleware enables buffering for /api/webhooks). Verify signature against raw bytes, then parse.
        if (Request.Body.CanSeek)
            Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);
        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[XENDIT] [WEBHOOK] Invalid JSON body");
            return BadRequest();
        }

        var expectedToken = _xenditOptions.WebhookToken;
        var publicKeyBase64 = _xenditOptions.PublicKey;
        var strictWebhookValidation = _xenditOptions.StrictWebhookValidation;
        var allowlistedIps = _xenditOptions.AllowedSourceIps;
        
        var sourceIp = ResolveSourceIp();
        var tokenValid = !string.IsNullOrEmpty(expectedToken) &&
                         !string.IsNullOrEmpty(callbackToken) &&
                         callbackToken == expectedToken;
        var ipAllowlisted = sourceIp != null && IsAllowlistedIp(sourceIp, allowlistedIps);

        // RSA Signature Verification (High Security) - use raw request body so verification matches Xendit's signed payload
        bool signatureValid = false;
        if (!string.IsNullOrEmpty(xenditSignature) && !string.IsNullOrEmpty(publicKeyBase64) && publicKeyBase64 != "REPLACE_WITH_XENDIT_PUBLIC_KEY")
        {
            try
            {
                signatureValid = VerifyXenditSignature(rawBody, xenditSignature, publicKeyBase64);
                
                if (signatureValid)
                    _logger.LogInformation("[XENDIT] Webhook RSA signature verified successfully");
                else
                    _logger.LogWarning("[XENDIT] Webhook RSA signature verification failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[XENDIT] Error during RSA signature verification");
            }
        }

        var anyValidAuth = tokenValid || signatureValid || ipAllowlisted;

        // Reject whenever any auth mechanism is configured but none validated (any environment),
        // or whenever strict validation is on. Only a fully unconfigured webhook — no token, no
        // RSA key, no IP allowlist — is allowed to pass through unverified (pure local testing).
        var anyAuthConfigured =
            !string.IsNullOrEmpty(expectedToken) ||
            (!string.IsNullOrEmpty(publicKeyBase64) && publicKeyBase64 != "REPLACE_WITH_XENDIT_PUBLIC_KEY") ||
            (allowlistedIps is { Length: > 0 });

        if (!anyValidAuth && (anyAuthConfigured || strictWebhookValidation))
        {
            _logger.LogWarning("Xendit webhook rejected: no valid authentication (token, signature, or IP)");
            return Unauthorized();
        }

        if (!anyValidAuth)
        {
            _logger.LogWarning("Xendit webhook: no auth configured; processing unverified webhook (local/testing only).");
        }

        // PII: do not log the payload (payer emails/names); the raw body is persisted
        // in PaymentWebhookEvent for audit. Event type + ids are logged during dispatch.
        _logger.LogInformation("[XENDIT] [WEBHOOK] Received Xendit webhook");

        // Persist the raw event before processing: idempotency (unique event key),
        // audit trail, and replayability if processing fails after Xendit stops retrying.
        var eventKey = Request.Headers["webhook-id"].FirstOrDefault();
        if (string.IsNullOrEmpty(eventKey))
            eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));

        var webhookEventType = payload.TryGetProperty("event", out var evtNameProp)
            ? evtNameProp.GetString() ?? "invoice"
            : "invoice";

        BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksReceived.Add(1,
            new KeyValuePair<string, object?>("type", CategorizeWebhook(webhookEventType)));

        var begin = await _eventStore.TryBeginAsync(PaymentProviders.Xendit, eventKey, webhookEventType, rawBody, ct);
        if (begin.Outcome == WebhookEventStore.BeginOutcome.Duplicate)
        {
            BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksDuplicate.Add(1);
            return Ok(new { received = true, duplicate = true });
        }
        var webhookEvent = begin.Event!;

        try
        {
            // Disbursement/payout and refund webhooks: event name + data.id, data.status
            if (payload.TryGetProperty("event", out var eventProp) && payload.TryGetProperty("data", out var dataProp))
            {
                var eventType = eventProp.GetString() ?? "";

                if (eventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase) && dataProp.ValueKind == JsonValueKind.Object)
                {
                    var refundId = dataProp.TryGetProperty("id", out var refundIdProp) ? refundIdProp.GetString() : null;
                    var refundStatus = dataProp.TryGetProperty("status", out var refundStatusProp) ? refundStatusProp.GetString() : null;
                    string? refundFailure = null;
                    if (dataProp.TryGetProperty("failure_code", out var refundFc) && refundFc.ValueKind != JsonValueKind.Null)
                        refundFailure = refundFc.GetString();

                    if (!string.IsNullOrEmpty(refundId) && !string.IsNullOrEmpty(refundStatus))
                    {
                        var refundResult = await _mediator.Send(new ProcessRefundWebhookCommand(PaymentProviders.Xendit, refundId, refundStatus, refundFailure), ct);
                        if (!refundResult.IsSuccess)
                            _logger.LogWarning("[XENDIT] [WEBHOOK] Refund webhook for {RefundId} not applied: {Error}", refundId, refundResult.Error);
                        _logger.LogInformation("[XENDIT] [WEBHOOK] Refund webhook processed - Id: {RefundId}, Event: {Event}, Status: {Status}", refundId, eventType, refundStatus);
                    }

                    await _eventStore.MarkProcessedAsync(webhookEvent, ct);
                    return Ok(new { received = true });
                }

                if (eventType.StartsWith("payout.", StringComparison.OrdinalIgnoreCase) && dataProp.ValueKind == JsonValueKind.Object)
                {
                    var disbursementId = dataProp.TryGetProperty("id", out var dataId) ? dataId.GetString() : null;
                    var statusStr = dataProp.TryGetProperty("status", out var dataStatus) ? dataStatus.GetString() : null;
                    string? failureReason = null;
                    if (dataProp.TryGetProperty("failure_code", out var fc) && fc.ValueKind != JsonValueKind.Null)
                        failureReason = fc.GetString();
                    if (dataProp.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind != JsonValueKind.Null)
                        failureReason = failureReason != null ? $"{failureReason} - {reasonProp.GetString()}" : reasonProp.GetString();
                    if (!string.IsNullOrEmpty(disbursementId) && !string.IsNullOrEmpty(statusStr) && _disbursementProcessor != null)
                    {
                        await _disbursementProcessor.ProcessAsync(PaymentProviders.Xendit, disbursementId, eventType, statusStr, failureReason, ct);
                        _logger.LogInformation("[XENDIT] [WEBHOOK] Disbursement webhook processed - Id: {DisbursementId}, Event: {Event}, Status: {Status}", disbursementId, eventType, statusStr);
                    }
                    await _eventStore.MarkProcessedAsync(webhookEvent, ct);
                    return Ok(new { received = true });
                }
            }

            // Invoice webhooks have top-level "id" and "status"
            if (!payload.TryGetProperty("id", out var idProp) || !payload.TryGetProperty("status", out var statusProp))
            {
                _logger.LogInformation("[XENDIT] [WEBHOOK] Ignoring non-invoice webhook (missing id/status); may be other event.");
                await _eventStore.MarkProcessedAsync(webhookEvent, ct);
                return Ok(new { received = true });
            }

            var invoiceId = idProp.GetString()!;
            var status = statusProp.GetString()!;
            string? externalId = null;
            if (payload.TryGetProperty("external_id", out var externalIdProp) && externalIdProp.ValueKind != JsonValueKind.Null)
            {
                externalId = externalIdProp.GetString();
            }
            
            DateTime? paidAt = null;
            if (payload.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind != JsonValueKind.Null)
            {
                paidAt = paidAtProp.GetDateTime();
            }

            decimal? paidAmount = null;
            if (payload.TryGetProperty("amount", out var amountProp) && amountProp.ValueKind != JsonValueKind.Null)
            {
                paidAmount = amountProp.GetDecimal();
            } else if (payload.TryGetProperty("paid_amount", out var paidAmountProp) && paidAmountProp.ValueKind != JsonValueKind.Null) {
                paidAmount = paidAmountProp.GetDecimal();
            }

            _logger.LogInformation("[XENDIT] [WEBHOOK] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Processing webhook - InvoiceId: {InvoiceId}, Status: {Status}, PaidAt: {PaidAt}, ExternalId: {ExternalId}", 
                DateTime.UtcNow, invoiceId, status, paidAt?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "null", externalId ?? "null");

            await _mediator.Send(new ProcessWebhookCommand(PaymentProviders.Xendit, invoiceId, status, paidAt, paidAmount), ct);
            
            _logger.LogInformation("[XENDIT] [WEBHOOK] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Webhook processed successfully - InvoiceId: {InvoiceId}", 
                DateTime.UtcNow, invoiceId);

            // Run synchronous post-processors (e.g. driver top-up crediting) so wallet updates
            // immediately. Failures are collected, not swallowed: every post-processor still
            // runs and the bus publish below still happens, but the exception is rethrown after
            // them so the caller returns non-2xx and Xendit redelivers. ACKing a webhook whose
            // wallet credit had not happened left the top-up to the reconciliation job alone.
            List<Exception>? postProcessorFailures = null;
            foreach (var post in _webhookPostProcessors)
            {
                try
                {
                    await post.ProcessAsync(PaymentProviders.Xendit, invoiceId, status, paidAt, paidAmount, externalId, currency: null, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[XENDIT] Webhook post-processor failed for invoice {InvoiceId}", invoiceId);
                    (postProcessorFailures ??= new List<Exception>()).Add(ex);
                }
            }

            // Staged on the PaymentDbContext outbox (GitLab #65) and flushed by MarkProcessedAsync
            // below, so the webhook record and the outgoing message commit together.
            //
            // This controller used to use IBus specifically because the only outbox lived on
            // BookingsDbContext, which it never saves. That is no longer true: there is an outbox
            // on this module's own context now, and staging survives a broker outage where an
            // immediate publish would simply have thrown and lost the message.
            _outboxPublisher.Publish(new PaymentCheckoutWebhookReceived
            {
                Provider = PaymentProviders.Xendit,
                ProviderPaymentId = invoiceId,
                Status = status,
                PaidAt = paidAt,
                ExternalId = externalId,
                PaidAmount = paidAmount
            });

            _logger.LogInformation("[XENDIT] [WEBHOOK] PaymentCheckoutWebhookReceived staged on the outbox - InvoiceId: {InvoiceId}, Status: {Status}",
                invoiceId, status);

            // Rethrown only after the bus publish, so the redundant consumer path is preserved.
            if (postProcessorFailures is { Count: > 0 })
                throw new AggregateException(
                    $"{postProcessorFailures.Count} webhook post-processor(s) failed for invoice {invoiceId}.",
                    postProcessorFailures);

            await _eventStore.MarkProcessedAsync(webhookEvent, ct);
            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksFailed.Add(1,
                new KeyValuePair<string, object?>("provider", PaymentProviders.Xendit),
                new KeyValuePair<string, object?>("type", "invoice"));
            _logger.LogError(ex, "[XENDIT] Error processing Xendit webhook");

            // Record the failure and return 500 so Xendit redelivers. Processing is
            // idempotent (status-guarded handlers + the stored event key), so a
            // retry after partial success is safe - unlike the previous behavior
            // of acknowledging with 200 and silently losing the event.
            await _eventStore.RecordErrorAsync(webhookEvent, ex.Message);

            return StatusCode(500, new { received = false });
        }
    }

    private static string CategorizeWebhook(string eventType)
    {
        if (eventType.StartsWith("payout.", StringComparison.OrdinalIgnoreCase)) return "payout";
        if (eventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase)) return "refund";
        return eventType == "invoice" ? "invoice" : "other";
    }

    private bool VerifyXenditSignature(string payload, string signature, string publicKeyBase64)
    {
        try
        {
            byte[] publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            byte[] signatureBytes = Convert.FromBase64String(signature);
            byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);

            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

            return rsa.VerifyData(
                payloadBytes,
                signatureBytes,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RSA Verification exception detail");
            return false;
        }
    }

    private string? ResolveSourceIp()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var candidate = forwardedFor?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(candidate)
            ? candidate
            : HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static bool IsAllowlistedIp(string sourceIp, IEnumerable<string> allowlist)
    {
        if (!IPAddress.TryParse(sourceIp, out var source))
            return false;

        foreach (var item in allowlist)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            if (IPAddress.TryParse(item.Trim(), out var exact) && Equals(source, exact))
                return true;
        }

        return false;
    }
}
