namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface ISmsProvider
{
    string Name { get; }

    Task<SmsSendResult> SendAsync(string toPhoneNumber, string message, CancellationToken ct = default);
}

public record SmsSendResult(
    bool Success,
    string Provider,
    string? Detail,
    string? ErrorCode,
    bool ShouldFailover = false);
