namespace BeeLogistics.Modules.Payment.Application.Services;

/// <summary>
/// Cancels provider checkouts for payments abandoned in Pending, so their links stop being payable.
///
/// A PayMongo checkout session has no server-side duration: left alone it stays payable until
/// PayMongo expires it on its own schedule. A customer paying against a stale link produces a
/// payment that settles with nothing to attach to - the orphan case reconciliation already detects
/// but cannot repair (GitLab #36). Xendit needs none of this; its invoices expire server-side via
/// <c>invoice_duration</c>, and its adapter implements the call as a no-op.
///
/// Separate from the Hangfire job that drives it so the behaviour is reachable by tests: the job
/// lives in the API project, which the test project does not reference, and this is the one part of
/// reconciliation that changes state at the provider.
/// </summary>
public interface IAbandonedCheckoutExpirer
{
    /// <param name="nowUtc">Reference time, injected so the grace window is testable without waiting.</param>
    /// <returns>How many checkouts were expired.</returns>
    Task<int> ExpireAsync(DateTime nowUtc, CancellationToken ct = default);
}
