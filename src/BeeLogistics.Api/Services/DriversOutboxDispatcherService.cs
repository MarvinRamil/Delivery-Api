using Hangfire;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Hangfire recurring job that polls the Drivers outbox table and dispatches
/// pending messages to MassTransit (which delivers them to consumers).
/// This provides at-least-once delivery with built-in retry and dashboard visibility.
/// </summary>
public class DriversOutboxDispatcherService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DriversOutboxDispatcherService> _logger;

    // Map event type names to their CLR types for deserialization
    private static readonly Dictionary<string, Type> _eventTypeMap = new()
    {
        [typeof(WithdrawalRequestedEvent).FullName!] = typeof(WithdrawalRequestedEvent),
        [typeof(WithdrawalCompletedEvent).FullName!] = typeof(WithdrawalCompletedEvent),
        [typeof(WithdrawalFailedEvent).FullName!] = typeof(WithdrawalFailedEvent),
    };

    public DriversOutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        ILogger<DriversOutboxDispatcherService> logger)
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
        var dbContext = scope.ServiceProvider.GetRequiredService<DriversDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        // Row locks (FOR UPDATE SKIP LOCKED) make concurrent runs safe: overlapping
        // Hangfire executions or multiple API instances each claim disjoint rows
        // instead of double-publishing the same message. The transaction holds the
        // locks until the batch is saved.
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var pendingMessages = await dbContext.OutboxMessages
            .FromSqlRaw(@"
                SELECT * FROM drivers.""OutboxMessages""
                WHERE ""ProcessedAt"" IS NULL AND ""RetryCount"" < {0}
                ORDER BY ""CreatedAt""
                LIMIT 50
                FOR UPDATE SKIP LOCKED", MaxRetryCount)
            .ToListAsync();

        if (pendingMessages.Count == 0)
            return;

        _logger.LogInformation("[OUTBOX] Processing {Count} pending outbox messages", pendingMessages.Count);

        foreach (var message in pendingMessages)
        {
            try
            {
                if (!_eventTypeMap.TryGetValue(message.EventType, out var eventType))
                {
                    _logger.LogWarning("[OUTBOX] Unknown event type {EventType}, skipping message {MessageId}", message.EventType, message.Id);
                    message.RecordFailure($"Unknown event type: {message.EventType}");
                    continue;
                }

                var eventPayload = JsonSerializer.Deserialize(message.Payload, eventType);
                if (eventPayload == null)
                {
                    _logger.LogWarning("[OUTBOX] Failed to deserialize message {MessageId} of type {EventType}", message.Id, message.EventType);
                    message.RecordFailure("Deserialization returned null");
                    continue;
                }

                // Publish to MassTransit (which routes to all registered consumers)
                await publishEndpoint.Publish(eventPayload, eventType);

                message.MarkAsProcessed();
                BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxDispatched.Add(1);
                _logger.LogInformation("[OUTBOX] Dispatched message {MessageId} ({EventType})", message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RecordFailure(ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message);
                BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxFailed.Add(1);
                if (message.RetryCount >= MaxRetryCount)
                {
                    BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPoison.Add(1);
                    _logger.LogError(ex,
                        "[OUTBOX] Message {MessageId} ({EventType}) reached max retries ({MaxRetries}) and will no longer be attempted; manual review required",
                        message.Id, message.EventType, MaxRetryCount);
                }
                else
                {
                    _logger.LogError(ex, "[OUTBOX] Failed to dispatch message {MessageId} ({EventType}), retry #{RetryCount}",
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
    /// Prunes dispatched outbox rows and reports what is still stuck (GitLab #65).
    /// </summary>
    /// <remarks>
    /// Two gaps this closes, both found while adding the Payment outbox:
    ///
    /// Nothing ever deleted processed rows, so this table grew without bound - every event ever
    /// published still sat here.
    ///
    /// And <c>OutboxPoison</c> only increments at the moment a message crosses the retry cutoff.
    /// It is a counter, so after a restart nobody can answer "how many are stuck right now". The
    /// standing backlog is the number worth alerting on, which needs measuring periodically
    /// rather than at the moment of failure - the same event-versus-sweep distinction
    /// <c>bee.payments.cancelled_unrefunded</c> draws.
    ///
    /// The Payment module needs no equivalent: MassTransit's delivery service removes its own
    /// rows once delivered.
    /// </remarks>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 1)]
    public async Task PruneAndReportAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DriversDbContext>();

        var cutoff = DateTime.UtcNow - ProcessedRetention;
        var deleted = await dbContext.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync();

        if (deleted > 0)
            _logger.LogInformation("[OUTBOX] Pruned {Count} dispatched outbox messages older than {Days} days", deleted, ProcessedRetention.TotalDays);

        var poisoned = await dbContext.OutboxMessages
            .CountAsync(m => m.ProcessedAt == null && m.RetryCount >= MaxRetryCount);

        var pending = await dbContext.OutboxMessages
            .CountAsync(m => m.ProcessedAt == null && m.RetryCount < MaxRetryCount);

        BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPoisonBacklog.Record(poisoned);
        BeeLogistics.Shared.Infrastructure.BeeMetrics.OutboxPendingBacklog.Record(pending);

        if (poisoned > 0)
            _logger.LogError(
                "[OUTBOX] {Count} outbox messages have exhausted their retries and will never be delivered without manual intervention.",
                poisoned);
    }
}
