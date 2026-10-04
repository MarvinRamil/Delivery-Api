namespace BeeLogistics.Modules.Payment.Application.Services;

/// <summary>
/// Counts online payments still sitting Paid against a Cancelled booking - money the customer has
/// not got back (GitLab #51).
///
/// This is the standing-backlog view. <c>BookingCancelledPaymentConsumer</c> already reacts to each
/// cancellation as it happens, but an event can be lost: the outbox can fail to flush, a consumer
/// can end up in an error queue. A booking that silently never reconciles is exactly the failure
/// this issue exists to stop, and the thing going missing is a customer's money, so the live path
/// gets a periodic backstop rather than being trusted alone.
///
/// Detection only - it moves nothing. Whether any of this is refunded automatically is governed by
/// <see cref="PaymentRefundOptions.AutoRefundCancelledBookings"/>, on the live path.
///
/// Separate from the Hangfire job that drives it for the same reason as
/// <see cref="IAbandonedCheckoutExpirer"/>: the job lives in the API project, which the test
/// project does not reference.
/// </summary>
public interface ICancelledBookingPaymentAuditor
{
    /// <param name="nowUtc">Reference time, injected so the lookback window is testable without waiting.</param>
    /// <returns>How many cancelled bookings still hold an unrefunded online payment.</returns>
    Task<int> AuditAsync(DateTime nowUtc, CancellationToken ct = default);
}
