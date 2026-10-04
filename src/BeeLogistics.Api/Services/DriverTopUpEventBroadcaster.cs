using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Broadcasts driver top-up paid events to SSE subscribers and SignalR so the app updates immediately.
/// Uses Redis Pub/Sub as a backplane to support multi-instance deployments.
/// </summary>
public sealed class DriverTopUpEventBroadcaster : IDriverTopUpEventBroadcaster, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DriverTopUpEventBroadcaster> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private const string RedisChannelPrefix = "driver-topup-paid:";

    // driverId -> list of channel writers (one per SSE connection on THIS instance)
    private readonly ConcurrentDictionary<Guid, List<ChannelWriter<DriverTopUpPaidPayload>>> _sseSubscribers = new();

    public DriverTopUpEventBroadcaster(
        IServiceScopeFactory scopeFactory, 
        IConnectionMultiplexer redis,
        ILogger<DriverTopUpEventBroadcaster> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _logger = logger;
        _subscriber = _redis.GetSubscriber();

        // Subscribe to pattern for all drivers - local connections will be notified when ANY instance publishes
        _subscriber.Subscribe(new RedisChannel(RedisChannelPrefix + "*", RedisChannel.PatternMode.Pattern), (channel, message) =>
        {
            try
            {
                var payload = JsonSerializer.Deserialize<DriverTopUpPaidPayload>(message.ToString());
                if (payload != null)
                {
                    NotifyLocalSubscribers(payload);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Redis Pub/Sub message for driver top-up");
            }
        });
    }

    public async Task PublishPaidAsync(DriverTopUpPaidPayload payload, CancellationToken ct = default)
    {
        // 1. Publish to Redis (Backplane) - this ensures ALL server instances see the event
        var message = JsonSerializer.Serialize(payload);
        await _subscriber.PublishAsync(RedisChannelPrefix + payload.DriverId, message);

        // 2. SignalR: resolve scoped INotificationService per call (singleton cannot hold scoped service)
        // SignalR handles its own scale-out if configured, but we call it here for the payload delivery
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notificationService.SendToGroupAsync(
                "driver-" + payload.DriverId,
                "TopUpPaid",
                new
                {
                    payload.TopUpId,
                    payload.DriverId,
                    payload.Amount,
                    payload.Status,
                    payload.PersonalBalance,
                    payload.TopUpBalance,
                    payload.PaidAtUtc
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR TopUpPaid send failed for driver {DriverId}", payload.DriverId);
        }
    }

    public async Task PublishFailedAsync(DriverTopUpFailedPayload payload, CancellationToken ct = default)
    {
        // SignalR only. The SSE stream is typed to paid events and is what credits the wallet in
        // the UI; a decline is a notification, not a balance change, so it does not belong there.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notificationService.SendToGroupAsync(
                "driver-" + payload.DriverId,
                "TopUpFailed",
                new
                {
                    payload.TopUpId,
                    payload.DriverId,
                    payload.Amount,
                    payload.Reason,
                    payload.CanRetry,
                    payload.CheckoutUrl,
                    payload.FailedAtUtc
                });
        }
        catch (Exception ex)
        {
            // Never fail webhook processing because a real-time push did not land - the failure
            // is already durable as a wallet transaction row the driver can see on next load.
            _logger.LogWarning(ex, "SignalR TopUpFailed send failed for driver {DriverId}", payload.DriverId);
        }
    }

    private void NotifyLocalSubscribers(DriverTopUpPaidPayload payload)
    {
        // SSE: write to all subscribers for this driver connected to THIS instance
        if (_sseSubscribers.TryGetValue(payload.DriverId, out var writers))
        {
            List<ChannelWriter<DriverTopUpPaidPayload>> copy;
            lock (writers)
            {
                copy = writers.ToList();
            }
            foreach (var w in copy)
            {
                try
                {
                    // Use TryWrite because we don't want to block the Redis subscriber thread
                    if (!w.TryWrite(payload))
                    {
                        _logger.LogWarning("SSE channel full for driver {DriverId}, message dropped", payload.DriverId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SSE write failed for driver {DriverId}", payload.DriverId);
                }
            }
        }
    }

    public async IAsyncEnumerable<DriverTopUpPaidPayload> SubscribeAsync(Guid driverId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<DriverTopUpPaidPayload>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var writers = _sseSubscribers.GetOrAdd(driverId, _ => new List<ChannelWriter<DriverTopUpPaidPayload>>());
        lock (writers)
        {
            writers.Add(channel.Writer);
        }

        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(ct))
            {
                yield return payload;
            }
        }
        finally
        {
            lock (writers)
            {
                writers.Remove(channel.Writer);
                channel.Writer.Complete();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _subscriber.UnsubscribeAll();
        }
        catch { /* ignored */ }
    }
}
