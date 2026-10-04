using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Modules.Payment.Presentation.Webhooks;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Presentation.Controllers;

/// <summary>
/// Handles PayMongo webhooks (POST api/webhooks/paymongo).
/// Auth: HMAC-SHA256 signature in the Paymongo-Signature header with timestamp
/// tolerance (replay protection). No IP allowlist — PayMongo does not publish a
/// stable source range; the per-endpoint signing secret is the auth factor.
/// NOTE: Inherits from ControllerBase (not BaseController) because external
/// webhooks are authenticated by signature, not JWT, and must be public.
/// </summary>
[Route("api/webhooks")]
[ApiController]
public class PayMongoWebhooksController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly PayMongoOptions _options;
    private readonly IPayMongoWebhookSignatureVerifier _signatureVerifier;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PayMongoWebhooksController> _logger;
    private readonly IPaymentOutboxPublisher _outboxPublisher;
    private readonly WebhookEventStore _eventStore;
    private readonly TimeProvider _clock;
    private readonly IEnumerable<IPaymentWebhookPostProcessor> _webhookPostProcessors;
    private readonly IDisbursementWebhookProcessor? _disbursementProcessor;
    private readonly IPayMongoAccountWebhookProcessor? _accountProcessor;
    private readonly IPayMongoQrWebhookProcessor? _qrProcessor;

    public PayMongoWebhooksController(
        IMediator mediator,
        IOptions<PayMongoOptions> options,
        IPayMongoWebhookSignatureVerifier signatureVerifier,
        IHostEnvironment environment,
        ILogger<PayMongoWebhooksController> logger,
        IPaymentOutboxPublisher outboxPublisher,
        WebhookEventStore eventStore,
        IEnumerable<IPaymentWebhookPostProcessor> webhookPostProcessors,
        TimeProvider? clock = null,
        IDisbursementWebhookProcessor? disbursementProcessor = null,
        IPayMongoAccountWebhookProcessor? accountProcessor = null,
        IPayMongoQrWebhookProcessor? qrProcessor = null)
    {
        _mediator = mediator;
        _options = options.Value;
        _signatureVerifier = signatureVerifier;
        _environment = environment;
        _logger = logger;
        _outboxPublisher = outboxPublisher;
        _eventStore = eventStore;
        _clock = clock ?? TimeProvider.System;
        _webhookPostProcessors = webhookPostProcessors ?? Array.Empty<IPaymentWebhookPostProcessor>();
        _disbursementProcessor = disbursementProcessor;
        _accountProcessor = accountProcessor;
        _qrProcessor = qrProcessor;
    }

    [HttpPost("paymongo")]
    public async Task<IActionResult> PayMongoWebhook(
        [FromHeader(Name = "Paymongo-Signature")] string? signatureHeader,
        CancellationToken ct)
    {
        // Read raw body first (middleware enables buffering for /api/webhooks).
        // The HMAC is computed over the raw bytes, so verify before parsing.
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
            _logger.LogWarning(ex, "[PAYMONGO] [WEBHOOK] Invalid JSON body");
            return BadRequest();
        }

        // Event envelope: { data: { id: "evt_...", attributes: { type, livemode, data: {...} } } }
        var hasEnvelope = payload.TryGetProperty("data", out var eventData) && eventData.ValueKind == JsonValueKind.Object;
        var eventAttributes = hasEnvelope && eventData.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs
            : default;

        var liveMode = hasEnvelope &&
            eventAttributes.ValueKind == JsonValueKind.Object &&
            eventAttributes.TryGetProperty("livemode", out var liveModeProp) &&
            liveModeProp.ValueKind == JsonValueKind.True;

        var signatureValid = !string.IsNullOrEmpty(signatureHeader) &&
            _signatureVerifier.Verify(
                rawBody,
                signatureHeader,
                _options.WebhookSecret,
                liveMode,
                TimeSpan.FromSeconds(_options.WebhookToleranceSeconds),
                _clock);

        if (!signatureValid)
        {
            // Reject a bad/missing signature whenever a secret is configured (any environment),
            // or whenever strict validation is on. Only a fully unconfigured secret — pure local
            // testing with no way to verify — is allowed to pass through unverified.
            var secretConfigured = !string.IsNullOrWhiteSpace(_options.WebhookSecret);
            if (secretConfigured || _options.StrictWebhookValidation)
            {
                _logger.LogWarning("[PAYMONGO] [WEBHOOK] Rejected: invalid or missing signature");
                return Unauthorized();
            }

            _logger.LogWarning("[PAYMONGO] [WEBHOOK] No webhook secret configured; processing unverified webhook (local/testing only).");
        }

        // PII: do not log the payload (payer emails/names); the raw body is persisted
        // in PaymentWebhookEvent for audit. Event type + ids are logged during dispatch.
        _logger.LogInformation("[PAYMONGO] [WEBHOOK] Received PayMongo webhook");

        var eventKey = hasEnvelope && eventData.TryGetProperty("id", out var evtId) && evtId.ValueKind == JsonValueKind.String
            ? evtId.GetString()!
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));

        var eventType = eventAttributes.ValueKind == JsonValueKind.Object &&
            eventAttributes.TryGetProperty("type", out var typeProp) &&
            typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()!
            : "unknown";

        BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksReceived.Add(1,
            new KeyValuePair<string, object?>("type", CategorizeWebhook(eventType)));

        var begin = await _eventStore.TryBeginAsync(PaymentProviders.PayMongo, eventKey, eventType, rawBody, ct);
        if (begin.Outcome == WebhookEventStore.BeginOutcome.Duplicate)
        {
            BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksDuplicate.Add(1);
            return Ok(new { received = true, duplicate = true });
        }
        var webhookEvent = begin.Event!;

        try
        {
            // The event resource lives at data.attributes.data
            var resource = eventAttributes.ValueKind == JsonValueKind.Object &&
                eventAttributes.TryGetProperty("data", out var resourceProp) &&
                resourceProp.ValueKind == JsonValueKind.Object
                ? resourceProp
                : default;

            if (eventType.StartsWith("checkout_session.", StringComparison.OrdinalIgnoreCase) && resource.ValueKind == JsonValueKind.Object)
            {
                await HandleCheckoutSessionEventAsync(eventType, resource, ct);
            }
            else if (eventType.Contains("refund", StringComparison.OrdinalIgnoreCase) && resource.ValueKind == JsonValueKind.Object)
            {
                await HandleRefundEventAsync(eventType, resource, ct);
            }
            else if (eventType.Equals("payment.failed", StringComparison.OrdinalIgnoreCase) && resource.ValueKind == JsonValueKind.Object)
            {
                await HandleFailedPaymentEventAsync(resource, ct);
            }
            else if (eventType.StartsWith("transfer.", StringComparison.OrdinalIgnoreCase) && resource.ValueKind == JsonValueKind.Object)
            {
                // Transfer webhooks may not be enabled on all PayMongo accounts, so
                // WithdrawalReconciliationService polls for withdrawals stuck in Approved
                // and resolves them through the same command this handler calls. That job
                // is the guaranteed path; this handler is an accelerator.
                await HandleTransferEventAsync(eventType, resource, ct);
            }
            else if (eventType.Equals("qr.paid", StringComparison.OrdinalIgnoreCase) && resource.ValueKind == JsonValueKind.Object)
            {
                await HandleQrPaidEventAsync(resource, ct);
            }
            else if (eventType.StartsWith("qr.", StringComparison.OrdinalIgnoreCase)
                     || eventType.StartsWith("qrph.", StringComparison.OrdinalIgnoreCase))
            {
                // qr.expired / qrph.expired. Nothing to do: our BeeWallet QRs are static and never
                // expire, so an expiry here concerns a dynamic QR we did not issue. Logged rather
                // than ignored silently so it is visible if that ever changes.
                _logger.LogInformation("[PAYMONGO] [WEBHOOK] [QR] Ignoring {EventType}", eventType);
            }
            else if (IsAccountLifecycleEvent(eventType) && resource.ValueKind == JsonValueKind.Object)
            {
                // Child-account onboarding (issue #91). Like transfers, these webhooks may not be
                // enabled on every PayMongo account, so the driver-triggered activate endpoint
                // remains the guaranteed path and this handler is an accelerator.
                await HandleAccountEventAsync(eventType, resource, ct);
            }
            else
            {
                _logger.LogInformation("[PAYMONGO] [WEBHOOK] Ignoring unhandled event type {EventType}", eventType);
            }

            await _eventStore.MarkProcessedAsync(webhookEvent, ct);
            return Ok(new { received = true });
        }
        catch (Exception ex)
        {
            BeeLogistics.Shared.Infrastructure.BeeMetrics.WebhooksFailed.Add(1,
                new KeyValuePair<string, object?>("provider", PaymentProviders.PayMongo),
                new KeyValuePair<string, object?>("type", CategorizeWebhook(eventType)));
            _logger.LogError(ex, "[PAYMONGO] Error processing PayMongo webhook");

            // Record the failure and return 500 so PayMongo redelivers. Processing is
            // idempotent (status-guarded handlers + the stored event key), so a retry
            // after partial success is safe.
            await _eventStore.RecordErrorAsync(webhookEvent, ex.Message);
            return StatusCode(500, new { received = false });
        }
    }

    private async Task HandleCheckoutSessionEventAsync(string eventType, JsonElement session, CancellationToken ct)
    {
        var sessionId = session.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(sessionId))
            return;

        var sessionAttributes = session.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs
            : default;

        // Xendit-compatible status vocabulary: downstream handlers/post-processors switch on
        // PAID/EXPIRED strings shared by both providers.
        //
        // Mapped explicitly, never by "anything that isn't paid must be expiry". An unrecognised
        // checkout_session.* event used to become EXPIRED, which would close a top-up the driver
        // was still mid-payment on - and expiry is close to a one-way door (#64). Unknown events
        // are ignored so the record stays live and reconciliation can still resolve it.
        var isPaid = eventType.Equals("checkout_session.payment.paid", StringComparison.OrdinalIgnoreCase);
        var isExpired = eventType.Equals("checkout_session.expired", StringComparison.OrdinalIgnoreCase);

        if (!isPaid && !isExpired)
        {
            _logger.LogInformation(
                "[PAYMONGO] [WEBHOOK] Ignoring unmapped checkout event {EventType} for session {SessionId}; leaving the record untouched.",
                eventType, sessionId);
            return;
        }

        var status = isPaid ? "PAID" : "EXPIRED";

        string? captureId = null;
        DateTime? paidAt = null;
        decimal? paidAmount = null;
        string? referenceId = null;
        string? currency = null;

        if (sessionAttributes.ValueKind == JsonValueKind.Object)
        {
            if (sessionAttributes.TryGetProperty("reference_number", out var refProp) && refProp.ValueKind == JsonValueKind.String)
                referenceId = refProp.GetString();

            if (sessionAttributes.TryGetProperty("payments", out var payments) &&
                payments.ValueKind == JsonValueKind.Array &&
                payments.GetArrayLength() > 0)
            {
                var payment = payments[0];
                captureId = payment.TryGetProperty("id", out var payId) && payId.ValueKind == JsonValueKind.String
                    ? payId.GetString()
                    : null;

                if (payment.TryGetProperty("attributes", out var payAttrs) && payAttrs.ValueKind == JsonValueKind.Object)
                {
                    if (payAttrs.TryGetProperty("amount", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number)
                        paidAmount = PayMongoGateway.FromCentavos(amountProp.GetInt64());
                    if (payAttrs.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind == JsonValueKind.Number)
                        paidAt = DateTimeOffset.FromUnixTimeSeconds(paidAtProp.GetInt64()).UtcDateTime;
                    // Passed downstream so a credit can refuse an amount charged in something
                    // other than the wallet's currency instead of taking it at face value (#66).
                    currency = ReadString(payAttrs, "currency");
                }
            }
        }

        _logger.LogInformation("[PAYMONGO] [WEBHOOK] Checkout event {EventType} - SessionId: {SessionId}, ReferenceId: {ReferenceId}",
            eventType, sessionId, referenceId ?? "n/a");

        var result = await _mediator.Send(new ProcessWebhookCommand(PaymentProviders.PayMongo, sessionId, status, paidAt, paidAmount), ct);
        if (result.ErrorKind == ResultErrorKind.NotFound)
            _logger.LogInformation("[PAYMONGO] [WEBHOOK] No booking payment matched session {SessionId}; may be a driver top-up", sessionId);
        else if (!result.IsSuccess)
            // Not a missing record - the payment exists and we refused to settle it (e.g. amount
            // mismatch). Never file this under "probably a top-up".
            _logger.LogError("[PAYMONGO] [WEBHOOK] Refused to process session {SessionId}: {Reason}", sessionId, result.Error);

        // Persist the capture id (pay_...) so refunds don't need an extra provider round-trip
        if (isPaid && captureId != null)
            await _mediator.Send(new SetPaymentCaptureIdCommand(PaymentProviders.PayMongo, sessionId, captureId), ct);

        // Synchronous post-processors (driver top-up crediting) so wallets update immediately.
        // Failures are collected rather than thrown immediately: every post-processor still gets
        // its turn, and the bus publish below still happens, so the redundant consumer path is
        // not lost just because the synchronous one broke.
        List<Exception>? postProcessorFailures = null;
        foreach (var post in _webhookPostProcessors)
        {
            try
            {
                await post.ProcessAsync(PaymentProviders.PayMongo, sessionId, status, paidAt, paidAmount, referenceId, currency, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PAYMONGO] Webhook post-processor failed for session {SessionId}", sessionId);
                (postProcessorFailures ??= new List<Exception>()).Add(ex);
            }
        }

        // Staged on the PaymentDbContext outbox (GitLab #65) and flushed by MarkProcessedAsync
        // at the end of the request, so the webhook being recorded as processed and the outgoing
        // message commit in one transaction. Neither can exist without the other.
        //
        // This also removes a failure loop: publishing straight to the broker meant a RabbitMQ
        // outage threw here, turning an already-credited top-up into a 500 and an endless
        // provider redelivery. Staging is a database write, so it succeeds while the broker is
        // down and the message goes out on recovery.
        _outboxPublisher.Publish(new PaymentCheckoutWebhookReceived
        {
            Provider = PaymentProviders.PayMongo,
            ProviderPaymentId = sessionId,
            Status = status,
            PaidAt = paidAt,
            ExternalId = referenceId,
            PaidAmount = paidAmount
        });

        // Surface the failure so the caller returns non-2xx and PayMongo redelivers. Swallowing
        // it here ACKed a webhook whose wallet credit had not happened, leaving the top-up to
        // the reconciliation job alone. Reprocessing is safe: the credit path is status-guarded.
        if (postProcessorFailures is { Count: > 0 })
            throw new AggregateException(
                $"{postProcessorFailures.Count} webhook post-processor(s) failed for session {sessionId}.",
                postProcessorFailures);
    }

    /// <summary>
    /// A payment attempt the provider declined (GitLab #64).
    /// </summary>
    /// <remarks>
    /// This event used to fall through to "unhandled event type" and be dropped, so a driver whose
    /// card was declined saw a pending top-up for 25 hours with no indication anything had gone
    /// wrong. It deliberately does <b>not</b> close the top-up: a declined attempt does not
    /// necessarily kill the checkout session, and the driver may retry on the same link.
    ///
    /// The resource here is a payment (pay_...), whose id matches nothing we stored, so the join
    /// back to the top-up is by our own reference number.
    /// </remarks>
    private async Task HandleFailedPaymentEventAsync(JsonElement payment, CancellationToken ct)
    {
        var paymentId = payment.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(paymentId))
            return;

        string? referenceId = null;
        string? failureReason = null;
        DateTime? failedAt = null;

        if (payment.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            referenceId = ReadString(attrs, "external_reference_number");
            failureReason = ReadString(attrs, "last_payment_error")
                            ?? ReadString(attrs, "failed_message")
                            ?? ReadString(attrs, "failed_code");

            if (attrs.TryGetProperty("updated_at", out var updatedProp) && updatedProp.ValueKind == JsonValueKind.Number)
                failedAt = DateTimeOffset.FromUnixTimeSeconds(updatedProp.GetInt64()).UtcDateTime;
        }

        _logger.LogInformation(
            "[PAYMONGO] [WEBHOOK] Payment {PaymentId} failed (reference {ReferenceId})",
            paymentId, referenceId ?? "n/a");

        // Via the post-processor hook, not a MediatR command: the failed top-up lives in the
        // Drivers module, which this module cannot reference. Same indirection the paid path uses.
        foreach (var post in _webhookPostProcessors)
        {
            try
            {
                await post.ProcessFailedAsync(PaymentProviders.PayMongo, paymentId, referenceId, failureReason, failedAt, ct);
            }
            catch (Exception ex)
            {
                // Not rethrown: no money moved, so a redelivery would only re-record a
                // notification. Unlike the paid path, there is nothing here worth a retry storm.
                _logger.LogError(ex, "[PAYMONGO] Failed-payment post-processor threw for payment {PaymentId}", paymentId);
            }
        }
    }

    /// <summary>
    /// Onboarding events for child accounts: identity verification outcomes, and activation or
    /// decline. Consumer accounts are what drivers get; merchant.* is matched too so a
    /// misconfigured account type is still routed rather than silently dropped.
    /// </summary>
    /// <summary>
    /// What a <c>qr.paid</c> payload says, once the shape quirks are resolved.
    /// </summary>
    /// <param name="IdempotencyKey">
    /// The per-payment transfer id. Never the QR id: a static BeeWallet QR keeps one id for every
    /// payment it ever receives, so keying on it would credit the first top-up and silently drop
    /// every one after.
    /// </param>
    /// <param name="ReferenceLabel">
    /// What the QR was issued for. A driver's BeeWallet top-up QR carries none; a cashbond QR
    /// carries <c>cashbond-{transactionId:N}</c>. This is the only routing key a cashbond/premium QR
    /// has — it credits the platform wallet, not a driver's child wallet, so the child account
    /// number and org id on the payload point at the platform, not at the driver to settle for.
    /// </param>
    public sealed record QrPayment(
        string? CreditAccountNumber, string? AccountId, decimal Amount, string? IdempotencyKey,
        string? ReferenceLabel);

    /// <summary>
    /// Reads a <c>qr.paid</c> resource. Exposed for tests: the ways this payload misleads — an
    /// amount in centavos, a <c>merchant_id</c> that appears both with and without its
    /// <c>org_</c> prefix, and an id that is stable across payments — are all silent when wrong.
    /// </summary>
    public static QrPayment ReadQrPayment(JsonElement resource)
    {
        var metadata = resource.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
            ? m
            : default;
        var hasMetadata = metadata.ValueKind == JsonValueKind.Object;

        var idempotencyKey = (hasMetadata ? ReadString(metadata, "transfer_id") : null)
                             ?? (hasMetadata ? ReadString(metadata, "reference_number") : null);

        // metadata.merchant_id carries the org_ prefix; the top-level one does not. Normalise so a
        // lookup by account id matches what we stored.
        var accountId = hasMetadata ? ReadString(metadata, "merchant_id") : null;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            var bare = ReadString(resource, "merchant_id");
            accountId = string.IsNullOrWhiteSpace(bare) ? null : $"org_{bare}";
        }

        var amount = resource.TryGetProperty("transaction_amount", out var amt) && amt.ValueKind == JsonValueKind.Number
            ? amt.GetInt64() / 100m
            : 0m;

        // PayMongo echoes it at the top level; metadata is tried too because the onboarding
        // payloads have already shown it moving between the two.
        var referenceLabel = ReadString(resource, "reference_label")
                             ?? (hasMetadata ? ReadString(metadata, "reference_label") : null);

        return new QrPayment(
            ReadString(resource, "credit_account_number"), accountId, amount, idempotencyKey, referenceLabel);
    }

    /// <summary>
    /// A driver's BeeWallet QR was paid. Credits the local mirror so the app stops showing a balance
    /// lower than PayMongo holds.
    /// </summary>
    private async Task HandleQrPaidEventAsync(JsonElement resource, CancellationToken ct)
    {
        if (_qrProcessor is null)
        {
            _logger.LogWarning("[PAYMONGO] [WEBHOOK] [QR] No processor registered; ignoring qr.paid");
            return;
        }

        var payment = ReadQrPayment(resource);

        if (string.IsNullOrWhiteSpace(payment.IdempotencyKey))
        {
            // Without a per-payment key a redelivery would credit twice. Refusing to guess.
            _logger.LogError(
                "[PAYMONGO] [WEBHOOK] [QR] qr.paid for {QrId} carried no transfer id; cannot credit safely",
                ReadString(resource, "id") ?? "n/a");
            return;
        }

        if (payment.Amount <= 0)
        {
            _logger.LogWarning("[PAYMONGO] [WEBHOOK] [QR] qr.paid carried no amount; ignoring");
            return;
        }

        await _qrProcessor.ProcessAsync(
            payment.CreditAccountNumber, payment.AccountId, payment.Amount, payment.IdempotencyKey!,
            payment.ReferenceLabel, ct);
    }

    private static bool IsAccountLifecycleEvent(string eventType)
        => eventType.StartsWith("account.identity_verification.", StringComparison.OrdinalIgnoreCase)
        || eventType.Equals("consumer.activated", StringComparison.OrdinalIgnoreCase)
        || eventType.Equals("consumer.declined", StringComparison.OrdinalIgnoreCase)
        || eventType.Equals("merchant.activated", StringComparison.OrdinalIgnoreCase)
        || eventType.Equals("merchant.declined", StringComparison.OrdinalIgnoreCase);

    private async Task HandleAccountEventAsync(string eventType, JsonElement resource, CancellationToken ct)
    {
        // PayMongo names this field differently across the onboarding events, so try each rather
        // than assuming one shape. Getting it wrong means the event is dropped and the driver sits
        // in "Setting up your account..." forever.
        var accountId = ReadString(resource, "merchant_id")
            ?? ReadString(resource, "account_id")
            ?? ReadString(resource, "id");

        if (string.IsNullOrWhiteSpace(accountId))
        {
            _logger.LogWarning(
                "[PAYMONGO] [WEBHOOK] [ACCOUNT] {EventType} carried no account id; cannot route it", eventType);
            return;
        }

        if (_accountProcessor is null)
        {
            _logger.LogWarning(
                "[PAYMONGO] [WEBHOOK] [ACCOUNT] No processor registered; ignoring {EventType} for {AccountId}",
                eventType, accountId);
            return;
        }

        var activationStatus = ReadString(resource, "activation_status");
        // PayMongo names this differently across events; try each rather than assuming.
        var failureReason = ReadString(resource, "failure_reason")
            ?? ReadString(resource, "failure_message")
            ?? ReadString(resource, "reason");

        _logger.LogInformation(
            "[PAYMONGO] [WEBHOOK] [ACCOUNT] {EventType} for {AccountId} (status {Status})",
            eventType, accountId, activationStatus ?? "n/a");

        await _accountProcessor.ProcessAsync(accountId, eventType, activationStatus, failureReason, ct);
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private async Task HandleRefundEventAsync(string eventType, JsonElement refund, CancellationToken ct)
    {
        var refundId = refund.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(refundId))
            return;

        string? status = null;
        string? failureReason = null;
        if (refund.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
                status = statusProp.GetString();
            if (attrs.TryGetProperty("failed_code", out var failedProp) && failedProp.ValueKind == JsonValueKind.String)
                failureReason = failedProp.GetString();
        }

        if (string.IsNullOrEmpty(status))
            return;

        // Normalize to the SUCCEEDED/FAILED vocabulary ProcessRefundWebhookHandler switches on
        var normalizedStatus = status.ToUpperInvariant();

        var result = await _mediator.Send(new ProcessRefundWebhookCommand(PaymentProviders.PayMongo, refundId, normalizedStatus, failureReason), ct);
        if (!result.IsSuccess)
            _logger.LogWarning("[PAYMONGO] [WEBHOOK] Refund webhook for {RefundId} not applied: {Error}", refundId, result.Error);

        _logger.LogInformation("[PAYMONGO] [WEBHOOK] Refund webhook processed - Id: {RefundId}, Event: {Event}, Status: {Status}",
            refundId, eventType, status);
    }

    private async Task HandleTransferEventAsync(string eventType, JsonElement transfer, CancellationToken ct)
    {
        var transferId = transfer.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(transferId) || _disbursementProcessor == null)
            return;

        string? status = null;
        string? failureReason = null;
        if (transfer.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
                status = statusProp.GetString();
            if (attrs.TryGetProperty("failure_code", out var failProp) && failProp.ValueKind == JsonValueKind.String)
                failureReason = failProp.GetString();
        }

        // The account subscribes to transfer.outward.successful / .failed, whose names already
        // state the outcome. Falling back to the name matters: dropping the event because
        // attributes.status was absent would leave the withdrawal stuck in Approved with the
        // driver's payout held, and (before this) no log line explaining why.
        status ??= StatusFromEventName(eventType);

        if (string.IsNullOrEmpty(status))
        {
            _logger.LogWarning(
                "[PAYMONGO] [WEBHOOK] Transfer event {Event} for {TransferId} carried no status and the "
                + "event name implies none; ignoring. The withdrawal reconciliation job will resolve it.",
                eventType, transferId);
            return;
        }

        await _disbursementProcessor.ProcessAsync(PaymentProviders.PayMongo, transferId, eventType, status, failureReason, ct);
        _logger.LogInformation("[PAYMONGO] [WEBHOOK] Transfer webhook processed - Id: {TransferId}, Event: {Event}, Status: {Status}",
            transferId, eventType, status);
    }

    /// <summary>
    /// Outcome implied by the event name, for payloads that omit attributes.status.
    /// Returns null for transfer events that are not terminal, so they stay a no-op.
    /// </summary>
    public static string? StatusFromEventName(string eventType)
    {
        if (eventType.EndsWith(".successful", StringComparison.OrdinalIgnoreCase) ||
            eventType.EndsWith(".succeeded", StringComparison.OrdinalIgnoreCase))
            return "SUCCEEDED";

        if (eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase) ||
            eventType.EndsWith(".returned", StringComparison.OrdinalIgnoreCase))
            return "FAILED";

        return null;
    }

    private static string CategorizeWebhook(string eventType)
    {
        if (eventType.StartsWith("transfer.", StringComparison.OrdinalIgnoreCase)) return "payout";
        if (eventType.Contains("refund", StringComparison.OrdinalIgnoreCase)) return "refund";
        if (eventType.StartsWith("checkout_session.", StringComparison.OrdinalIgnoreCase)) return "invoice";
        return "other";
    }
}
