using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Domain;

/// <summary>
/// Every webhook received from a payment provider, persisted before processing.
/// Provides idempotency (unique EventKey), an audit trail, and replayability:
/// a failed webhook can be re-processed from the stored payload instead of
/// being lost when the provider stops retrying.
/// </summary>
public class PaymentWebhookEvent : Entity
{
    public string Provider { get; private set; } = null!;
    /// <summary>
    /// Provider-supplied unique id for this delivery (Xendit webhook-id header),
    /// or a content hash when the provider doesn't send one.
    /// </summary>
    public string EventKey { get; private set; } = null!;
    public string EventType { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public string? Error { get; private set; }

    private PaymentWebhookEvent() { }

    public static PaymentWebhookEvent Create(string provider, string eventKey, string eventType, string payload)
    {
        return new PaymentWebhookEvent
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            EventKey = eventKey,
            EventType = eventType,
            Payload = payload,
            ReceivedAt = DateTime.UtcNow
        };
    }

    public void MarkProcessed()
    {
        ProcessedAt = DateTime.UtcNow;
        Error = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordError(string error)
    {
        Error = error.Length > 2000 ? error[..2000] : error;
        UpdatedAt = DateTime.UtcNow;
    }
}
