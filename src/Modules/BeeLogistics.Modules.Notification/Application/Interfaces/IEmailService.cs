namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface IEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
    Task SendTemplatedAsync(string templateName, EmailMessage message, Dictionary<string, string> placeholders, CancellationToken ct = default);
}

public record EmailMessage(
    string To,
    string Subject,
    string Body,
    bool IsHtml = true,
    string? From = null,
    string? FromName = null,
    List<string>? Cc = null,
    List<string>? Bcc = null
);
