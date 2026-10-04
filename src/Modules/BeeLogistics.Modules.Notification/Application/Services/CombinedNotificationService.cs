using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Application.Services;

/// <summary>
/// Combined notification service that sends notifications via both SignalR (real-time, in-process)
/// and push (queued onto the SendPush bus via the dispatcher, so it is recorded and delivered by
/// SendPushConsumer). This ensures notifications work whether the app is open (SignalR) or closed (push).
/// </summary>
public class CombinedNotificationService
{
    private readonly INotificationService _signalRService;
    private readonly IPushDispatcher _pushDispatcher;
    private readonly ILogger<CombinedNotificationService> _logger;

    public CombinedNotificationService(
        INotificationService signalRService,
        IPushDispatcher pushDispatcher,
        ILogger<CombinedNotificationService> logger)
    {
        _signalRService = signalRService;
        _pushDispatcher = pushDispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Send notification to a user via both SignalR and FCM
    /// </summary>
    public async Task SendToUserAsync(
        string userId,
        string appType,
        string method,
        object signalRData,
        string? pushTitle = null,
        string? pushBody = null,
        object? pushData = null,
        CancellationToken ct = default)
    {
        // Send via SignalR (for when app is open)
        try
        {
            await _signalRService.SendToUserAsync(userId, method, signalRData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SignalR notification to user {UserId}", userId);
        }

        // Queue push (for when app is closed or in background) — routed through the SendPush
        // bus so it is recorded and delivered by SendPushConsumer.
        try
        {
            // Use pushTitle/pushBody if provided, otherwise extract from signalRData
            var title = pushTitle ?? ExtractTitle(signalRData);
            var body = pushBody ?? ExtractBody(signalRData);
            var data = BuildDataMap(pushData ?? signalRData);

            await _pushDispatcher.DispatchAsync(new SendPushRequested
            {
                UserId = userId,
                AppType = appType,
                Title = title,
                Body = body,
                Data = data
            }, "module", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue push notification to user {UserId}", userId);
        }
    }

    /// <summary>
    /// Flattens a push data payload to the string→string map FCM/Expo require. Rich objects
    /// (used for the real-time SignalR payload) have their public properties stringified.
    /// </summary>
    private static Dictionary<string, string>? BuildDataMap(object? data)
    {
        if (data == null) return null;
        if (data is Dictionary<string, string> already) return already;

        var map = new Dictionary<string, string>();
        foreach (var prop in data.GetType().GetProperties())
        {
            var value = prop.GetValue(data);
            if (value != null) map[prop.Name] = value.ToString() ?? string.Empty;
        }
        return map.Count > 0 ? map : null;
    }

    private string ExtractTitle(object data)
    {
        // Try to extract title from data object
        var titleProp = data.GetType().GetProperty("Title") ?? 
                       data.GetType().GetProperty("title");
        if (titleProp != null)
        {
            return titleProp.GetValue(data)?.ToString() ?? "New Notification";
        }

        // Try to extract from message
        var messageProp = data.GetType().GetProperty("Message") ?? 
                         data.GetType().GetProperty("message");
        if (messageProp != null)
        {
            var message = messageProp.GetValue(data)?.ToString();
            if (!string.IsNullOrEmpty(message) && message.Length > 50)
            {
                return message.Substring(0, 50) + "...";
            }
            return message ?? "New Notification";
        }

        return "New Notification";
    }

    private string ExtractBody(object data)
    {
        var messageProp = data.GetType().GetProperty("Message") ?? 
                         data.GetType().GetProperty("message");
        if (messageProp != null)
        {
            return messageProp.GetValue(data)?.ToString() ?? "";
        }

        return "";
    }
}

