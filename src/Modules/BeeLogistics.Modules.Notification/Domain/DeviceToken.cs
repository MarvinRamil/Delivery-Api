using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Notification.Domain;

/// <summary>
/// Represents a device token for push notifications
/// </summary>
public class DeviceToken : Entity
{
    public string UserId { get; set; } = null!;
    public string Token { get; set; } = null!;
    public string Platform { get; set; } = null!; // "ios" or "android"
    public string AppType { get; set; } = null!; // "customer" or "driver"
    public string? TokenType { get; set; } // "fcm" (Android) | "apns" (iOS) | "expo"; null for legacy/unflagged tokens
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    private DeviceToken() { } // For EF Core

    public DeviceToken(string userId, string token, string platform, string appType, string? tokenType = null)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Token = token;
        Platform = platform;
        AppType = appType;
        TokenType = tokenType;
        LastUsedAt = DateTime.UtcNow;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }
}

