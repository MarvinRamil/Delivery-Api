namespace BeeLogistics.Modules.Notification.Application.DTOs;

/// <summary>
/// Request to send a push notification. Provide one target type: deviceToken, deviceTokens, userId+appType, userIds+appType, or appType+burst.
/// </summary>
public record SendPushRequestDto
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public Dictionary<string, string>? Data { get; init; }
    public string? DeviceToken { get; init; }
    public List<string>? DeviceTokens { get; init; }
    public string? UserId { get; init; }
    public List<string>? UserIds { get; init; }
    public string? AppType { get; init; }
    public bool Burst { get; init; }
}
