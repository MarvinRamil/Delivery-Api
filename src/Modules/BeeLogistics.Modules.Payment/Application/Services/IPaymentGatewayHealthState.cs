namespace BeeLogistics.Modules.Payment.Application.Services;

/// <summary>
/// What real traffic has most recently told us about each payment provider.
///
/// Readiness reads this instead of calling the provider on every probe. A synthetic probe on the
/// readiness path is its own outage risk - it adds provider latency to every health check, burns
/// rate limit, and reports on a request nobody made. Observed outcomes reflect the traffic that
/// actually matters and cost nothing to read.
///
/// Two distinct failure modes are tracked, because the circuit breaker only catches one of them:
///
/// <list type="bullet">
/// <item><b>Unreachable / erroring</b> - the resilience pipeline's circuit breaker opens on 5xx and
/// timeouts, and reports it here through its OnOpened/OnClosed callbacks.</item>
/// <item><b>Credentials rejected</b> - a revoked or mis-rotated API key returns 401/403, which the
/// standard resilience handler treats as a legitimate answer rather than a failure. The breaker
/// stays closed and readiness would have stayed green, so this is recorded separately.</item>
/// </list>
/// </summary>
public interface IPaymentGatewayHealthState
{
    /// <summary>A call completed with a status the provider considers successful.</summary>
    void RecordSuccess(string provider, DateTime nowUtc);

    /// <summary>A call was rejected as unauthenticated or forbidden (401/403).</summary>
    void RecordCredentialsRejected(string provider, int statusCode, DateTime nowUtc);

    /// <summary>A call failed to complete - transport error, timeout, or an open circuit.</summary>
    void RecordFailure(string provider, string reason, DateTime nowUtc);

    void RecordCircuitState(string provider, GatewayCircuitState state, DateTime nowUtc);

    /// <summary>Current view for one provider. Never null: an unseen provider reads as <see cref="GatewayCircuitState.Unknown"/>.</summary>
    PaymentGatewayHealthSnapshot GetSnapshot(string provider);
}

public enum GatewayCircuitState
{
    /// <summary>No traffic observed yet. Not a fault - a freshly started process has simply not called the provider.</summary>
    Unknown = 0,
    Closed = 1,
    HalfOpen = 2,
    Open = 3
}

/// <param name="CredentialsRejected">
/// True when the most recent outcome was a 401/403. Cleared by any later success, so a single
/// transient rejection does not latch readiness to unhealthy forever.
/// </param>
public sealed record PaymentGatewayHealthSnapshot(
    string Provider,
    GatewayCircuitState Circuit,
    bool CredentialsRejected,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string? LastFailureReason);
