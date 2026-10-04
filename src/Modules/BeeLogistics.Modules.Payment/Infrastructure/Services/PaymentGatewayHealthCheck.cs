using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Application.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// Readiness check for the payment provider, driven by observed traffic rather than a synthetic
/// probe (GitLab #35).
///
/// Before this, <c>/health/ready</c> covered Postgres, Redis and the bus but said nothing about the
/// gateway: if PayMongo went unreachable or its key was revoked, readiness stayed green and the
/// first sign of trouble was customers failing to check out. Startup fail-fast catches a *missing*
/// key, never an invalid one.
///
/// Reports on the <b>active</b> gateway only. Xendit is configured but unused, so its circuit
/// tripping must not pull this instance out of rotation for a provider nothing is calling.
///
/// Lives in the Payment module rather than beside RedisHealthCheck in the API project because it is
/// this module's concern - and because that keeps it inside the test project's reach.
/// </summary>
public sealed class PaymentGatewayHealthCheck : IHealthCheck
{
    private readonly IPaymentGatewayHealthState _state;
    private readonly string _activeProvider;

    public PaymentGatewayHealthCheck(IPaymentGatewayHealthState state, IOptions<PaymentGatewayOptions> options)
    {
        _state = state;
        _activeProvider = PaymentProviders.Normalize(options.Value.ActiveGateway) ?? PaymentProviders.PayMongo;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _state.GetSnapshot(_activeProvider);

        var data = new Dictionary<string, object>
        {
            ["provider"] = snapshot.Provider,
            ["circuit"] = snapshot.Circuit.ToString(),
            ["lastSuccessUtc"] = snapshot.LastSuccessUtc?.ToString("O") ?? "never",
            ["lastFailureUtc"] = snapshot.LastFailureUtc?.ToString("O") ?? "never"
        };

        if (snapshot.LastFailureReason is not null)
            data["lastFailureReason"] = snapshot.LastFailureReason;

        // Credentials first: it is the more actionable of the two, and a rejected key can coexist
        // with a closed circuit because 401/403 never trips the breaker.
        if (snapshot.CredentialsRejected)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"{snapshot.Provider} rejected our credentials - check the API key. {snapshot.LastFailureReason}", data: data));
        }

        return Task.FromResult(snapshot.Circuit switch
        {
            GatewayCircuitState.Open => HealthCheckResult.Unhealthy(
                $"{snapshot.Provider} circuit is open - calls are failing. {snapshot.LastFailureReason ?? "no detail"}", data: data),

            // Half-open means the breaker is letting a trial call through. Degraded rather than
            // unhealthy: recovery is in progress and flapping readiness would make it worse.
            GatewayCircuitState.HalfOpen => HealthCheckResult.Degraded(
                $"{snapshot.Provider} circuit is half-open - recovering", data: data),

            // No calls yet. A freshly started process has simply not talked to the provider, and
            // reporting unhealthy would fail readiness at boot and block every deploy.
            GatewayCircuitState.Unknown => HealthCheckResult.Healthy(
                $"{snapshot.Provider} not yet called", data: data),

            _ => HealthCheckResult.Healthy($"{snapshot.Provider} reachable", data: data)
        });
    }
}
