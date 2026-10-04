using Hangfire;
using BeeLogistics.Modules.Payment.Infrastructure;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Hangfire recurring job that polls the Payment outbox table and dispatches
/// pending messages to MassTransit (which delivers them to consumers).
///
/// Mirrors <see cref="DriversOutboxDispatcherService"/> exactly. Payment's own hand-rolled outbox,
/// not MassTransit's built-in EF Core bus outbox - see the comment where
/// AddEntityFrameworkOutbox&lt;PaymentDbContext&gt;() was removed in Program.cs for why (MassTransit
/// 8.x supports only one DbContext per bus for the transactional bus outbox).
/// </summary>
public class PaymentOutboxDispatcherService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentOutboxDispatcherService> _logger;

    private static readonly KeyValuePair<string, object?> ModuleTag = new("module", "payment");

    // Map event type names to their CLR types for deserialization
    private static readonly Dictionary<string, Type> _eventTypeMap = new()
    {
        [typeof(PaymentRefundedEvent).FullName!] = typeof(PaymentRefundedEvent),
        [typeof(PaymentCheckoutWebhookReceived).FullName!] = typeof(PaymentCheckoutWebhookReceived),
    };

    public PaymentOutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentOutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Messages that failed this many times are considered poison and skipped;
    /// they stay in the table (ProcessedAt NULL, LastError set) for manual review.
    /// </summary>
    private const int MaxRetryCount = 10;

    /// <summary>
    /// Called by Hangfire recurring job. Processes all pending outbox messages.
    /// </summary>
    public async Task ProcessPendingMessagesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        // Row locks (FOR UPDATE SKIP LOCKED) make concurrent runs safe: overlapping
        // Hangfire executions or multiple API instances each claim disjoint rows
        // instead of double-publishing the same message. The transaction holds the
        // locks until the batch is saved.
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var pendingMessages = await dbContext.OutboxMessages
            .FromSqlRaw(@"
                SELECT * FROM payment.""OutboxMessages""
                WHERE ""ProcessedAt"" IS NULL AND ""RetryCount"" < {0}
                ORDER BY ""CreatedAt""
                LIMIT 50
                FOR UPDATE SKIP LOCKED", MaxRetryCount)
            .ToListAsync();

        if (pendingMessages.Count == 0)
            return;

        _logger.LogInformation("[PAYMENT-OUTBOX] Processing {Count} pending outbox messages", pendingMessages.Count);

        foreach (var message in pendingMessages)
        {
            try
            {
                if (!_eventTypeMap.TryGetValue(message.EventType, out var eventType))
                {
                    _logger.LogWarning("[PAYMENT-OUTBOX] Unknown event type {EventType}, skipping message {MessageId}", message.EventType, message.Id);
                    message.RecordFailure($"Unknown event type: {message.EventType}");
                    continue;
                }

                var eventPayload = JsonSerializer.Deserialize(message.Payload, eventType);
                if (eventPayload == null)
                {
                    _logger.LogWarning("[PAYMENT-OUTBOX] Failed to deserialize message {MessageId} of type {EventType}", message.Id, message.EventType);
                    message.RecordFailure("Deserialization returned null");
                    continue;
                }

                // Publish to MassTransit (which routes to all registered consumers)
                await publishEndpoint.Publish(eventPayload, eventType);

                message.MarkAsProcessed();
                BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxDispatched.Add(1, ModuleTag);
                _logger.LogInformation("[PAYMENT-OUTBOX] Dispatched message {MessageId} ({EventType})", message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RecordFailure(ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message);
                BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxFailed.Add(1, ModuleTag);
                if (message.RetryCount >= MaxRetryCount)
                {
                    BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPoison.Add(1, ModuleTag);
                    _logger.LogError(ex,
                        "[PAYMENT-OUTBOX] Message {MessageId} ({EventType}) reached max retries ({MaxRetries}) and will no longer be attempted; manual review required",
                        message.Id, message.EventType, MaxRetryCount);
                }
                else
                {
                    _logger.LogError(ex, "[PAYMENT-OUTBOX] Failed to dispatch message {MessageId} ({EventType}), retry #{RetryCount}",
                        message.Id, message.EventType, message.RetryCount);
                }
            }
        }

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Rows older than this are deleted once dispatched. Long enough to still be useful when
    /// investigating something from last week, short enough that the table stays small.
    /// </summary>
    private static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Prunes dispatched outbox rows and reports what is still stuck.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 1)]
    public async Task PruneAndReportAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

        var cutoff = DateTime.UtcNow - ProcessedRetention;
        var deleted = await dbContext.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync();

        if (deleted > 0)
            _logger.LogInformation("[PAYMENT-OUTBOX] Pruned {Count} dispatched outbox messages older than {Days} days", deleted, ProcessedRetention.TotalDays);

        var poisoned = await dbContext.OutboxMessages
            .CountAsync(m => m.ProcessedAt == null && m.RetryCount >= MaxRetryCount);

        var pending = await dbContext.OutboxMessages
            .CountAsync(m => m.ProcessedAt == null && m.RetryCount < MaxRetryCount);

        BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPoisonBacklog.Record(poisoned, ModuleTag);
        BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPendingBacklog.Record(pending, ModuleTag);

        if (poisoned > 0)
            _logger.LogError(
                "[PAYMENT-OUTBOX] {Count} outbox messages have exhausted their retries and will never be delivered without manual intervention.",
                poisoned);
    }
}
