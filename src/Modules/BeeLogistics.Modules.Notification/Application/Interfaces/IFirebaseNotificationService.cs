namespace BeeLogistics.Modules.Notification.Application.Interfaces;

/// <summary>
/// Service for sending push notifications via Firebase Cloud Messaging
/// </summary>
public interface IFirebaseNotificationService
{
    /// <summary>
    /// Send notification to a single device token
    /// </summary>
    Task<bool> SendToDeviceAsync(string deviceToken, string title, string body, object? data = null, CancellationToken ct = default);

    /// <summary>
    /// Send notification to multiple device tokens
    /// </summary>
    Task<int> SendToMultipleDevicesAsync(IEnumerable<string> deviceTokens, string title, string body, object? data = null, CancellationToken ct = default);

    /// <summary>
    /// Send notification to all devices of a user
    /// </summary>
    Task<int> SendToUserAsync(string userId, string appType, string title, string body, object? data = null, CancellationToken ct = default);
}

