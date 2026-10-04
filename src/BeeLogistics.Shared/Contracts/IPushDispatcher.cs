namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// The single entry point for sending a push notification. Any module can inject this
/// (it lives in Shared, so no dependency on the Notification module) to fire a push
/// without knowing about FCM/Expo. The implementation persists a Queued
/// PushNotificationRecord and publishes <see cref="SendPushRequested"/> onto the
/// SendPush queue, where SendPushConsumer delivers it and updates the record to
/// Sent/Failed. Fire-and-forget: returns as soon as the record is written and the
/// message is published.
/// </summary>
public interface IPushDispatcher
{
    /// <summary>
    /// Persists a Queued record for this request, publishes it to the SendPush queue,
    /// and returns the new record's Id (so callers can surface it for status lookup).
    /// </summary>
    /// <param name="request">Push payload + target (device/user/broadcast).</param>
    /// <param name="source">Origin for the log, e.g. "backoffice", "module", "driver-approval".</param>
    Task<Guid> DispatchAsync(SendPushRequested request, string? source = null, CancellationToken ct = default);
}
