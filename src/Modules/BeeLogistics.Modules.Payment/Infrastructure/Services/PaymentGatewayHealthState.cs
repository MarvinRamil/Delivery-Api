using System.Collections.Concurrent;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Services;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <inheritdoc cref="IPaymentGatewayHealthState"/>
public sealed class PaymentGatewayHealthState : IPaymentGatewayHealthState
{
    private sealed class ProviderState
    {
        public GatewayCircuitState Circuit = GatewayCircuitState.Unknown;
        public DateTime? LastSuccessUtc;
        public DateTime? LastCredentialRejectionUtc;
        public DateTime? LastFailureUtc;
        public string? LastFailureReason;
    }

    private readonly ConcurrentDictionary<string, ProviderState> _states = new(StringComparer.OrdinalIgnoreCase);

    private ProviderState For(string provider) =>
        _states.GetOrAdd(PaymentProviders.Normalize(provider) ?? provider, _ => new ProviderState());

    public void RecordSuccess(string provider, DateTime nowUtc)
    {
        var state = For(provider);
        lock (state)
        {
            state.LastSuccessUtc = nowUtc;

            // A success also means the circuit is passing traffic. Without this a process that
            // never sees a circuit transition would sit at Unknown forever.
            if (state.Circuit == GatewayCircuitState.Unknown)
                state.Circuit = GatewayCircuitState.Closed;
        }
    }

    public void RecordCredentialsRejected(string provider, int statusCode, DateTime nowUtc)
    {
        var state = For(provider);
        lock (state)
        {
            state.LastCredentialRejectionUtc = nowUtc;
            state.LastFailureUtc = nowUtc;
            state.LastFailureReason = $"Provider rejected our credentials with HTTP {statusCode}";
        }
    }

    public void RecordFailure(string provider, string reason, DateTime nowUtc)
    {
        var state = For(provider);
        lock (state)
        {
            state.LastFailureUtc = nowUtc;
            state.LastFailureReason = reason;
        }
    }

    public void RecordCircuitState(string provider, GatewayCircuitState circuitState, DateTime nowUtc)
    {
        var state = For(provider);
        lock (state)
        {
            state.Circuit = circuitState;

            if (circuitState == GatewayCircuitState.Open)
            {
                state.LastFailureUtc = nowUtc;
                state.LastFailureReason ??= "Circuit breaker opened";
            }
        }
    }

    public PaymentGatewayHealthSnapshot GetSnapshot(string provider)
    {
        var normalized = PaymentProviders.Normalize(provider) ?? provider;

        if (!_states.TryGetValue(normalized, out var state))
            return new PaymentGatewayHealthSnapshot(normalized, GatewayCircuitState.Unknown, false, null, null, null);

        lock (state)
        {
            // Only the *most recent* outcome counts. A rejection followed by a success means the
            // key was rotated correctly and service resumed, so readiness must recover on its own
            // rather than staying latched until someone restarts the process.
            var credentialsRejected =
                state.LastCredentialRejectionUtc.HasValue &&
                (!state.LastSuccessUtc.HasValue || state.LastCredentialRejectionUtc > state.LastSuccessUtc);

            return new PaymentGatewayHealthSnapshot(
                normalized,
                state.Circuit,
                credentialsRejected,
                state.LastSuccessUtc,
                state.LastFailureUtc,
                state.LastFailureReason);
        }
    }
}
