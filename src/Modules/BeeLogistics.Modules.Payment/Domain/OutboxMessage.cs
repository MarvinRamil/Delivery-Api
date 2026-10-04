namespace BeeLogistics.Modules.Payment.Domain;

/// <summary>
/// Transactional outbox message. Saved in the same DB transaction as the business data
/// to guarantee at-least-once event delivery. A Hangfire poller dispatches pending messages.
///
/// Payment's own hand-rolled outbox, mirroring the Drivers module's (see
/// BeeLogistics.Modules.Drivers.Domain.OutboxMessage), rather than MassTransit's built-in EF Core
/// bus outbox. MassTransit 8.x only supports one DbContext per bus for the transactional bus
/// outbox (UseBusOutbox) - registering it on both BookingsDbContext and PaymentDbContext caused
/// their two BusOutboxDeliveryService background loops to share one IBusOutboxNotification
/// singleton and race on its internal CancellationTokenSource, crashing the host with a
/// NullReferenceException in BusOutboxNotification.WaitForDelivery. Multi-DbContext support only
/// landed in MassTransit 9.2.0. This sidesteps the limitation entirely rather than reverting to a
/// single shared MassTransit outbox, which would resurrect the exact bug UseBusOutbox on this
/// context was added to fix (GitLab #65: a Payment-module IPublishEndpoint injection silently
/// staging onto a DbContext this module never saves).
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Full type name of the event (e.g. "BeeLogistics.Shared.Contracts.PaymentRefundedEvent")</summary>
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
