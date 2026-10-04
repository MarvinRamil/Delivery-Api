namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

/// <summary>
/// Schedules the per-booking, one-shot "advance this broadcast" job that drives offer progression
/// without a population-wide pulse. Implemented over a durable scheduler (Hangfire) so the timer
/// survives restarts. Keeps the Application layer free of the scheduler dependency.
/// </summary>
public interface IBroadcastScheduler
{
    /// <summary>
    /// Schedule the next advance for <paramref name="bookingId"/> shortly after <paramref name="waveExpiryUtc"/>
    /// (so offers are genuinely past their deadline when it runs).
    /// </summary>
    void ScheduleAdvance(Guid bookingId, DateTime waveExpiryUtc);
}
