using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Hubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Broadcasts booking payment paid events to SSE subscribers and SignalR so the app updates immediately.
/// </summary>
public sealed class BookingPaymentEventBroadcaster : IBookingPaymentEventBroadcaster
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingPaymentEventBroadcaster> _logger;

    // customerId -> list of channel writers (one per SSE connection)
    private readonly ConcurrentDictionary<Guid, List<ChannelWriter<BookingPaymentPaidPayload>>> _sseSubscribers = new();

    public BookingPaymentEventBroadcaster(IServiceScopeFactory scopeFactory, ILogger<BookingPaymentEventBroadcaster> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task PublishPaidAsync(BookingPaymentPaidPayload payload, CancellationToken ct = default)
    {
        var publishStartTime = DateTime.UtcNow;
        _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Publishing payment paid event - PaymentId: {PaymentId}, BookingId: {BookingId}, CustomerId: {CustomerId}, Amount: {Amount}", 
            publishStartTime, payload.PaymentId, payload.BookingId == Guid.Empty ? "null" : payload.BookingId.ToString(), payload.CustomerId, payload.Amount);
        
        // SignalR: resolve scoped INotificationService per call (singleton cannot hold scoped service)
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notificationService.SendToGroupAsync(
                "customer-" + payload.CustomerId,
                "BookingPaymentPaid",
                new
                {
                    payload.PaymentId,
                    payload.BookingId,
                    payload.CustomerId,
                    payload.Amount,
                    payload.Status,
                    payload.PaidAtUtc
                });
            _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SignalR notification sent - CustomerId: {CustomerId}", 
                DateTime.UtcNow, payload.CustomerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SignalR BookingPaymentPaid send failed for customer {CustomerId}", 
                DateTime.UtcNow, payload.CustomerId);
        }

        // SSE: write to all subscribers for this customer
        if (_sseSubscribers.TryGetValue(payload.CustomerId, out var writers))
        {
            List<ChannelWriter<BookingPaymentPaidPayload>> copy;
            lock (writers)
            {
                copy = writers.ToList();
            }
            _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Found {Count} SSE subscribers for CustomerId: {CustomerId}", 
                DateTime.UtcNow, copy.Count, payload.CustomerId);
            
            foreach (var w in copy)
            {
                try
                {
                    await w.WriteAsync(payload, ct);
                    _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE event written to subscriber - CustomerId: {CustomerId}, PaymentId: {PaymentId}", 
                        DateTime.UtcNow, payload.CustomerId, payload.PaymentId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE write failed for customer {CustomerId}", 
                        DateTime.UtcNow, payload.CustomerId);
                }
            }
        }
        else
        {
            _logger.LogWarning("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] No SSE subscribers found for CustomerId: {CustomerId} - Event may be missed if SSE connects later", 
                DateTime.UtcNow, payload.CustomerId);
        }
        
        _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment paid event published - PaymentId: {PaymentId}, CustomerId: {CustomerId}, Duration: {Duration}ms", 
            DateTime.UtcNow, payload.PaymentId, payload.CustomerId, (DateTime.UtcNow - publishStartTime).TotalMilliseconds);
    }

    public async IAsyncEnumerable<BookingPaymentPaidPayload> SubscribeAsync(Guid customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var subscribeTime = DateTime.UtcNow;
        _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] ===== SSE SUBSCRIBER ADDED ===== CustomerId: {CustomerId}", 
            subscribeTime, customerId);
        
        var channel = Channel.CreateUnbounded<BookingPaymentPaidPayload>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var writers = _sseSubscribers.GetOrAdd(customerId, _ => new List<ChannelWriter<BookingPaymentPaidPayload>>());
        lock (writers)
        {
            writers.Add(channel.Writer);
            _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE subscriber added to list - CustomerId: {CustomerId}, TotalSubscribers: {Count}", 
                DateTime.UtcNow, customerId, writers.Count);
        }

        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(ct))
            {
                var receiveTime = DateTime.UtcNow;
                _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE subscriber received payload - CustomerId: {CustomerId}, PaymentId: {PaymentId}", 
                    receiveTime, customerId, payload.PaymentId);
                yield return payload;
            }
        }
        finally
        {
            lock (writers)
            {
                writers.Remove(channel.Writer);
                channel.Writer.Complete();
                _logger.LogInformation("[XENDIT] [EVENT_BROADCASTER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] SSE subscriber removed - CustomerId: {CustomerId}, RemainingSubscribers: {Count}", 
                    DateTime.UtcNow, customerId, writers.Count);
            }
        }
    }
}
