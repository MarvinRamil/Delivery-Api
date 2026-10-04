using System.Diagnostics;
using System.Net;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Shared.Infrastructure;

namespace BeeLogistics.Modules.Payment.Infrastructure.Http;

/// <summary>
/// Observes every outbound call to a payment provider in one place: records latency by operation
/// and feeds <see cref="IPaymentGatewayHealthState"/> so readiness reflects real traffic.
///
/// A handler rather than instrumentation at the call sites - each gateway adapter has around a
/// dozen of them, and anything added per-method is one new endpoint away from being forgotten.
///
/// Sits outside the resilience pipeline, so what it records is the effective outcome the caller
/// saw: retries already exhausted, and a rejection from an open circuit counted as the failure it
/// is rather than being invisible.
/// </summary>
public sealed class PaymentGatewayTelemetryHandler : DelegatingHandler
{
    private readonly string _provider;
    private readonly IPaymentGatewayHealthState _healthState;

    public PaymentGatewayTelemetryHandler(string provider, IPaymentGatewayHealthState healthState)
    {
        _provider = provider;
        _healthState = healthState;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = DescribeOperation(request);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var now = DateTime.UtcNow;

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The one case the circuit breaker cannot catch: the standard resilience handler
                // treats 401/403 as a valid answer, not a fault, so the breaker stays closed and
                // readiness would otherwise stay green while every checkout fails.
                _healthState.RecordCredentialsRejected(_provider, (int)response.StatusCode, now);
            }
            else if (response.IsSuccessStatusCode)
            {
                _healthState.RecordSuccess(_provider, now);
            }
            else if ((int)response.StatusCode >= 500)
            {
                _healthState.RecordFailure(_provider, $"HTTP {(int)response.StatusCode} from provider", now);
            }
            // 4xx other than 401/403 is the provider answering us correctly about a bad request.
            // That is our bug, not the integration being down, so it must not affect readiness.

            Record(operation, started, OutcomeFor(response.StatusCode));
            return response;
        }
        catch (Exception ex)
        {
            _healthState.RecordFailure(_provider, ex.GetType().Name + ": " + ex.Message, DateTime.UtcNow);
            Record(operation, started, "failure");
            throw;
        }
    }

    private void Record(string operation, long startedTimestamp, string outcome) =>
        BeeMetrics.GatewayRequestDuration.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("provider", _provider),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));

    private static string OutcomeFor(HttpStatusCode status) => (int)status switch
    {
        >= 500 => "server_error",
        >= 400 => "client_error",
        _ => "success"
    };

    /// <summary>
    /// Collapses a request into a bounded operation name such as <c>POST /checkout_sessions</c>.
    ///
    /// Only the first meaningful path segment is kept. Resource ids (<c>cs_abc123</c>,
    /// <c>rf_xyz</c>) must never reach a metric tag - one time series per payment would be an
    /// unbounded cardinality explosion, which is exactly why the built-in HttpClient
    /// instrumentation declines to tag the path at all.
    /// </summary>
    public static string DescribeOperation(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        var path = request.RequestUri?.AbsolutePath;

        if (string.IsNullOrWhiteSpace(path))
            return method;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // Skip an API version prefix (/v1/, /v2/) so PayMongo and Xendit describe the same
            // resource the same way.
            if (segment.Length > 1 && segment[0] is 'v' or 'V' && segment[1..].All(char.IsDigit))
                continue;

            return $"{method} /{segment}";
        }

        return method;
    }
}
