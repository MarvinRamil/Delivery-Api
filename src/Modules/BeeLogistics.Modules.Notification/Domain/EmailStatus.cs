namespace BeeLogistics.Modules.Notification.Domain;

public enum EmailStatus
{
    Queued = 0,
    Processing = 1,
    Sent = 2,
    Failed = 3,
    DeadLetter = 4
}

public class EmailRecord
{
    public Guid Id { get; set; }
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? From { get; set; }
    public string? FromName { get; set; }
    public EmailStatus Status { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public string? HangfireJobId { get; set; }
}
