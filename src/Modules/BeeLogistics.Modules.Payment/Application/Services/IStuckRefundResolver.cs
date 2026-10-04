namespace BeeLogistics.Modules.Payment.Application.Services;

/// <summary>
/// Resolves payments stuck in RefundPending by asking the provider what actually happened,
/// instead of only logging/counting them.
///
/// A payment lands here when its refund.* webhook never arrived to finalize it - lost delivery,
/// or (since the refund-race fix) a gateway call that threw with the true outcome unknown at the
/// time. Either way, the provider itself always knows the truth; this asks it directly rather
/// than leaving the payment to wait indefinitely for a webhook that may never come.
///
/// Separate from the Hangfire job that drives it so the behaviour is reachable by tests, matching
/// <see cref="IAbandonedCheckoutExpirer"/>: the job lives in the API project, which the test
/// project does not reference.
/// </summary>
public interface IStuckRefundResolver
{
    /// <param name="olderThanUtc">Only payments RefundPending since before this are resolved.</param>
    /// <returns>How many stuck refunds were resolved (settled Refunded or returned to Paid).</returns>
    Task<int> ResolveAsync(DateTime olderThanUtc, CancellationToken ct = default);
}
