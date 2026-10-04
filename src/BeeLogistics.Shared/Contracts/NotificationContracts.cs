using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Query to get minimal booking info for sending notifications.
/// This is a cross-module query that can be handled by the Sales module.
/// </summary>
public record GetBookingForNotificationQuery(Guid BookingId) : IRequest<Result<BookingNotificationInfo>>;

/// <summary>
/// Minimal booking information needed for sending notifications.
/// </summary>
public record BookingNotificationInfo(Guid BookingId, string BookingNumber, Guid CustomerId);

/// <summary>
/// Query to get minimal customer info from a list of dispatch IDs.
/// Used for sending notifications to customers when manifest status changes.
/// </summary>
public record GetCustomersFromDispatchesQuery(IEnumerable<Guid> DispatchIds) : IRequest<Result<IReadOnlyList<CustomerNotificationInfo>>>;

/// <summary>
/// Minimal customer information for notifications.
/// </summary>
public record CustomerNotificationInfo(Guid CustomerId, Guid BookingId, string BookingNumber);

/// <summary>
/// Fire-and-forget request for another bee-backend module to send a push notification
/// (published over MassTransit; consumed by the Notification module's SendPushConsumer).
/// Provide exactly one target: DeviceToken, DeviceTokens, UserId+AppType, UserIds+AppType,
/// or AppType+Burst. Mirrors the push payload of the HTTP send endpoint.
/// </summary>
public record SendPushRequested
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

    /// <summary>
    /// Id of the pre-created PushNotificationRecord (set by IPushDispatcher). The
    /// SendPushConsumer updates this record to Sent/Failed after delivery. Empty when
    /// a message is published without going through the dispatcher (no record to update).
    /// </summary>
    public Guid RecordId { get; init; }
}

/// <summary>
/// Unified fire-and-forget request to send across any of email / SMS / push
/// (published over MassTransit; consumed by the Notification module's
/// SendNotificationConsumer). Populate the sub-object for each requested channel.
/// </summary>
public record SendNotificationRequested
{
    public List<string> Channels { get; init; } = new(); // "email", "sms", "push"
    public EmailChannelPayload? Email { get; init; }
    public SmsChannelPayload? Sms { get; init; }
    public SendPushRequested? Push { get; init; }
}

public record EmailChannelPayload(
    string To,
    string Subject,
    string Body,
    bool IsHtml = true,
    List<string>? Cc = null,
    List<string>? Bcc = null,
    string? TemplateName = null,
    Dictionary<string, string>? Placeholders = null
);

public record SmsChannelPayload(string To, string Message);

