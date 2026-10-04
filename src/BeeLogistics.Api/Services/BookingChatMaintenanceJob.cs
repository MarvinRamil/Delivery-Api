using BeeLogistics.Modules.Messaging.Application.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Hangfire wrapper for booking-chat maintenance (#78).
/// </summary>
/// <remarks>
/// Thin on purpose: the behaviour lives in the Messaging module, where the test project can reach
/// it without referencing Hangfire. Only scheduling belongs here — same split as
/// <see cref="LocationHistoryRetentionJob"/> and PaymentReconciliationService.
/// </remarks>
public class BookingChatMaintenanceJob
{
    private readonly IBookingRoomLifecycleService _lifecycle;
    private readonly IBookingRoomBackstopSweep _backstop;
    private readonly ILogger<BookingChatMaintenanceJob> _logger;

    public BookingChatMaintenanceJob(
        IBookingRoomLifecycleService lifecycle,
        IBookingRoomBackstopSweep backstop,
        ILogger<BookingChatMaintenanceJob> logger)
    {
        _lifecycle = lifecycle;
        _backstop = backstop;
        _logger = logger;
    }

    /// <summary>Retires rooms whose booking finished long enough ago.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 1)]
    public async Task FreezeAsync()
    {
        var frozen = await _lifecycle.FreezeFinishedRoomsAsync(DateTime.UtcNow);
        if (frozen > 0) _logger.LogInformation("Booking chat: froze {Count} room(s)", frozen);
    }

    /// <summary>
    /// Deletes long-frozen rooms from Synapse. Off-peak: it is a bulk delete on the homeserver, and
    /// each room is a separate admin call.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 1)]
    public async Task PurgeAsync()
    {
        var purged = await _lifecycle.PurgeExpiredRoomsAsync(DateTime.UtcNow);
        if (purged > 0) _logger.LogInformation("Booking chat: purged {Count} room(s) from Synapse", purged);
    }

    /// <summary>
    /// Provisions rooms the live path failed to create. See <see cref="IBookingRoomBackstopSweep"/>
    /// for why this exists at all.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 1)]
    public async Task BackstopAsync() => await _backstop.SweepAsync(DateTime.UtcNow);
}
