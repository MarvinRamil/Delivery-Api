using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Payment.Presentation.Webhooks;

/// <summary>
/// Shared webhook idempotency/audit store used by all provider webhook endpoints.
/// A delivery is recorded exactly once per (provider, event key).
/// </summary>
/// <remarks>
/// What this actually guarantees, precisely, because the difference matters:
///
/// <list type="bullet">
/// <item>A delivery of an event already <b>finished</b> (ProcessedAt set) is reported Duplicate
/// and never reprocessed.</item>
/// <item>Two deliveries racing to <b>insert</b> the same event are resolved by the unique index;
/// the loser is reported Duplicate.</item>
/// <item>A delivery arriving while another is <b>still in flight</b> (row exists, ProcessedAt
/// null) is <b>not</b> serialised - both proceed. The same applies to a redelivery of an event
/// whose previous attempt failed, which is deliberate: that is how a failed webhook gets retried
/// at all.</item>
/// </list>
///
/// So this store does not by itself make processing single-threaded, and callers must not assume
/// it does. Concurrent processing is safe because the handlers underneath are individually
/// idempotent - status-guarded updates plus xmin optimistic concurrency on the wallet, which
/// makes the losing writer fail rather than double-apply. Removing either of those protections
/// would make this a double-credit bug, not a style question.
/// </remarks>
public sealed class WebhookEventStore
{
    private readonly PaymentDbContext _dbContext;
    private readonly ILogger<WebhookEventStore> _logger;

    public WebhookEventStore(PaymentDbContext dbContext, ILogger<WebhookEventStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public enum BeginOutcome { Begun, Duplicate }

    public sealed record BeginResult(BeginOutcome Outcome, PaymentWebhookEvent? Event);

    /// <summary>
    /// Persists the raw event before processing (idempotency + audit + replayability).
    /// Returns Duplicate when this delivery was already processed or is being handled
    /// concurrently; otherwise returns the tracked event to mark processed/errored later.
    /// </summary>
    public async Task<BeginResult> TryBeginAsync(string provider, string eventKey, string eventType, string rawBody, CancellationToken ct)
    {
        var webhookEvent = await _dbContext.WebhookEvents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Provider == provider && e.EventKey == eventKey, ct);

        if (webhookEvent?.ProcessedAt != null)
        {
            _logger.LogInformation("[WEBHOOK] Duplicate delivery {Provider}/{EventKey} already processed; acknowledging", provider, eventKey);
            return new BeginResult(BeginOutcome.Duplicate, webhookEvent);
        }

        if (webhookEvent == null)
        {
            webhookEvent = PaymentWebhookEvent.Create(provider, eventKey, eventType, rawBody);
            _dbContext.WebhookEvents.Add(webhookEvent);
            try
            {
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Unique index hit: a concurrent delivery of the same event is being handled
                _logger.LogInformation("[WEBHOOK] Concurrent duplicate delivery {Provider}/{EventKey}; acknowledging", provider, eventKey);
                return new BeginResult(BeginOutcome.Duplicate, null);
            }
        }

        return new BeginResult(BeginOutcome.Begun, webhookEvent);
    }

    public async Task MarkProcessedAsync(PaymentWebhookEvent webhookEvent, CancellationToken ct)
    {
        webhookEvent.MarkProcessed();
        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>Records the failure so the provider's redelivery can be correlated; never throws.</summary>
    public async Task RecordErrorAsync(PaymentWebhookEvent webhookEvent, string error)
    {
        try
        {
            webhookEvent.RecordError(error);
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception saveEx)
        {
            _logger.LogError(saveEx, "[WEBHOOK] Failed to record webhook processing error for {Provider}/{EventKey}",
                webhookEvent.Provider, webhookEvent.EventKey);
        }
    }
}
