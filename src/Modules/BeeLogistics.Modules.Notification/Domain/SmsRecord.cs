namespace BeeLogistics.Modules.Notification.Domain;

public enum SmsStatus
{
    Sent = 0,
    Failed = 1
}

public class SmsRecord
{
    public Guid Id { get; set; }

    public string To { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public SmsStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SentAt { get; set; }
}
