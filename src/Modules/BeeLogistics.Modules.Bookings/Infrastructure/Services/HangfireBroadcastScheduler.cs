using BeeLogistics.Modules.Bookings.Application.Interfaces;
using Hangfire;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

/// <summary>
/// Hangfire-backed <see cref="IBroadcastScheduler"/>. Enqueues a one-shot job per booking; Hangfire
/// persists it in Postgres so the deadline survives process restarts.
/// </summary>
public class HangfireBroadcastScheduler : IBroadcastScheduler
{
    // Fire a few seconds after expiry so offers are reliably past their deadline when the advance runs.
    private static readonly TimeSpan Buffer = TimeSpan.FromSeconds(5);

    public void ScheduleAdvance(Guid bookingId, DateTime waveExpiryUtc)
    {
        var delay = (waveExpiryUtc - DateTime.UtcNow) + Buffer;
        if (delay < Buffer)
            delay = Buffer;
        BackgroundJob.Schedule<BookingBroadcastQueueService>(s => s.AdvanceBroadcastAsync(bookingId), delay);
    }
}
