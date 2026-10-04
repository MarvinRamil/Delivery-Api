namespace BeeLogistics.Modules.Notification.Application.DTOs;

/// <summary>
/// Unified send request. Set <see cref="Channels"/> to any of "email", "sms", "push"
/// and populate the matching sub-object(s). Channels fire independently — one failing
/// does not abort the others (see per-channel results in the response).
/// </summary>
public record SendNotificationRequestDto
{
    public List<string> Channels { get; init; } = new();
    public EmailChannelDto? Email { get; init; }
    public SmsChannelDto? Sms { get; init; }
    public SendPushRequestDto? Push { get; init; }
}

public record EmailChannelDto(
    string To,
    string Subject,
    string Body,
    bool IsHtml = true,
    List<string>? Cc = null,
    List<string>? Bcc = null,
    string? TemplateName = null,
    Dictionary<string, string>? Placeholders = null
);

public record SmsChannelDto(
    string To,      // normalized, e.g. 639171234567
    string Message
);

/// <summary>Per-channel outcome. A null section means that channel was not requested.</summary>
public record SendNotificationResponseDto
{
    public EmailChannelResult? Email { get; init; }
    public SmsChannelResult? Sms { get; init; }
    public PushChannelResult? Push { get; init; }
    public List<string> Errors { get; init; } = new();
}

public record EmailChannelResult(bool Queued);
public record SmsChannelResult(bool Sent, string? Error = null);
// Push is async (queued onto the SendPush bus). Delivery outcome/device count is
// recorded against RecordId — see GET api/notifications/history.
public record PushChannelResult(bool Queued, Guid? RecordId = null, string? Error = null);
