namespace BeeLogistics.Modules.Notification.Domain;

public enum PushStatus
{
    Queued = 0,
    Sent = 1,
    Failed = 2
}

/// <summary>
/// Audit record for a push notification send. Written as Queued by the dispatcher when
/// the request is published to the SendPush queue, then updated to Sent/Failed (with the
/// device count or error) by SendPushConsumer after delivery. Plain POCO, mirroring
/// <see cref="EmailRecord"/> / <see cref="SmsRecord"/> — manages its own Id/CreatedAt.
/// </summary>
public class PushNotificationRecord
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>How the target was expressed: device | devices | user | users | broadcast.</summary>
    public string TargetMode { get; set; } = string.Empty;

    /// <summary>The userId or token targeted; null for a broadcast.</summary>
    public string? TargetValue { get; set; }

    /// <summary>"customer" | "driver" when targeting by user/broadcast; null otherwise.</summary>
    public string? AppType { get; set; }

    public PushStatus Status { get; set; }

    /// <summary>Number of devices the send reached (populated once delivered).</summary>
    public int DevicesSent { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Origin of the send: "backoffice", "module", "driver-approval", etc.</summary>
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SentAt { get; set; }

    public DateTime? FailedAt { get; set; }
}
