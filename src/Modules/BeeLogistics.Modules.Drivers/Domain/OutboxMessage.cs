namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// Transactional outbox message. Saved in the same DB transaction as the business data
/// to guarantee at-least-once event delivery. A Hangfire poller dispatches pending messages.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    
    /// <summary>Full type name of the event (e.g. "BeeLogistics.Shared.Contracts.WithdrawalRequestedEvent")</summary>
    public string EventType { get; private set; } = string.Empty;
    
    /// <summary>JSON-serialized event payload</summary>
    public string Payload { get; private set; } = string.Empty;
    
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    
    /// <summary>Set when the message has been successfully dispatched</summary>
    public DateTime? ProcessedAt { get; private set; }
    
    /// <summary>Number of dispatch attempts (for monitoring)</summary>
    public int RetryCount { get; private set; }
    
    /// <summary>Last error message if dispatch failed</summary>
    public string? LastError { get; private set; }

    private OutboxMessage() { } // EF

    public OutboxMessage(string eventType, string payload)
    {
        EventType = eventType;
        Payload = payload;
    }

    public void MarkAsProcessed()
    {
        ProcessedAt = DateTime.UtcNow;
    }

    public void RecordFailure(string error)
    {
        RetryCount++;
        LastError = error;
    }
}
