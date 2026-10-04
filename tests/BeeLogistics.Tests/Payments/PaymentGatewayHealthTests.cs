using System.Net;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Modules.Payment.Infrastructure.Http;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Gateway readiness (GitLab #35).
///
/// /health/ready covered Postgres, Redis and the bus but said nothing about the payment provider,
/// so an unreachable gateway or a revoked API key left readiness green until customers started
/// failing to check out.
///
/// The check reads outcomes observed from real traffic rather than probing the provider. A probe on
/// the readiness path would add provider latency to every check, burn rate limit, and report on a
/// request nobody made.
/// </summary>
public class PaymentGatewayHealthTests
{
    private readonly PaymentGatewayHealthState _state = new();

    private PaymentGatewayHealthCheck Check(string activeGateway = PaymentProviders.PayMongo) =>
        new(_state, Options.Create(new PaymentGatewayOptions { ActiveGateway = activeGateway }));

    private Task<HealthCheckResult> Run(string activeGateway = PaymentProviders.PayMongo) =>
        Check(activeGateway).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    // ---- Readiness verdicts ------------------------------------------------------------------

    /// <summary>
    /// A freshly started process has not called the provider yet. Reporting unhealthy here would
    /// fail readiness at boot and block every deploy.
    /// </summary>
    [Fact]
    public async Task Healthy_before_any_traffic()
    {
        var result = await Run();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Healthy_once_calls_are_succeeding()
    {
        _state.RecordSuccess(PaymentProviders.PayMongo, DateTime.UtcNow);

        var result = await Run();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_when_the_circuit_is_open()
    {
        _state.RecordCircuitState(PaymentProviders.PayMongo, GatewayCircuitState.Open, DateTime.UtcNow);

        var result = await Run();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("circuit is open", result.Description);
    }

    /// <summary>
    /// Half-open means the breaker is letting a trial call through. Flapping readiness during
    /// recovery would pull instances in and out of rotation for no benefit.
    /// </summary>
    [Fact]
    public async Task Degraded_while_the_circuit_is_recovering()
    {
        _state.RecordCircuitState(PaymentProviders.PayMongo, GatewayCircuitState.HalfOpen, DateTime.UtcNow);

        var result = await Run();

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    /// <summary>
    /// The gap the circuit breaker cannot cover: 401/403 is a valid answer to the resilience
    /// pipeline, so the breaker stays closed while every checkout fails.
    /// </summary>
    [Fact]
    public async Task Unhealthy_when_credentials_are_rejected_even_with_a_closed_circuit()
    {
        _state.RecordSuccess(PaymentProviders.PayMongo, DateTime.UtcNow.AddMinutes(-5));
        _state.RecordCredentialsRejected(PaymentProviders.PayMongo, 401, DateTime.UtcNow);

        var result = await Run();

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("credentials", result.Description);
    }

    /// <summary>
    /// A correctly rotated key must recover readiness on its own, without a restart.
    /// </summary>
    [Fact]
    public async Task Recovers_when_a_later_call_succeeds()
    {
        _state.RecordCredentialsRejected(PaymentProviders.PayMongo, 401, DateTime.UtcNow.AddMinutes(-5));
        _state.RecordSuccess(PaymentProviders.PayMongo, DateTime.UtcNow);

        var result = await Run();

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// Xendit is implemented but unused. Its circuit tripping must not pull this instance out of
    /// rotation for a provider nothing is calling.
    /// </summary>
    [Fact]
    public async Task Inactive_gateway_failing_does_not_affect_readiness()
    {
        _state.RecordCircuitState(PaymentProviders.Xendit, GatewayCircuitState.Open, DateTime.UtcNow);
        _state.RecordCredentialsRejected(PaymentProviders.Xendit, 401, DateTime.UtcNow);

        var result = await Run(activeGateway: PaymentProviders.PayMongo);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_on_whichever_gateway_is_active()
    {
        _state.RecordCircuitState(PaymentProviders.Xendit, GatewayCircuitState.Open, DateTime.UtcNow);

        var result = await Run(activeGateway: PaymentProviders.Xendit);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ---- State bookkeeping -------------------------------------------------------------------

    [Fact]
    public void A_success_moves_an_unseen_provider_off_unknown()
    {
        _state.RecordSuccess(PaymentProviders.PayMongo, DateTime.UtcNow);

        Assert.Equal(GatewayCircuitState.Closed, _state.GetSnapshot(PaymentProviders.PayMongo).Circuit);
    }

    [Fact]
    public void Providers_are_tracked_independently()
    {
        _state.RecordCircuitState(PaymentProviders.Xendit, GatewayCircuitState.Open, DateTime.UtcNow);

        Assert.Equal(GatewayCircuitState.Open, _state.GetSnapshot(PaymentProviders.Xendit).Circuit);
        Assert.Equal(GatewayCircuitState.Unknown, _state.GetSnapshot(PaymentProviders.PayMongo).Circuit);
    }

    // ---- Telemetry handler -------------------------------------------------------------------

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status);

    private async Task<HttpResponseMessage> SendThroughHandler(HttpStatusCode status, string url)
    {
        var handler = new PaymentGatewayTelemetryHandler(PaymentProviders.PayMongo, _state)
        {
            InnerHandler = new StubHandler(Respond(status))
        };
        using var invoker = new HttpMessageInvoker(handler);
        return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url), CancellationToken.None);
    }

    [Fact]
    public async Task Handler_records_a_success()
    {
        await SendThroughHandler(HttpStatusCode.OK, "https://api.paymongo.com/v1/checkout_sessions");

        Assert.NotNull(_state.GetSnapshot(PaymentProviders.PayMongo).LastSuccessUtc);
        Assert.False(_state.GetSnapshot(PaymentProviders.PayMongo).CredentialsRejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Handler_flags_rejected_credentials(HttpStatusCode status)
    {
        await SendThroughHandler(status, "https://api.paymongo.com/v1/checkout_sessions");

        Assert.True(_state.GetSnapshot(PaymentProviders.PayMongo).CredentialsRejected);
    }

    /// <summary>
    /// A 400 or 404 is the provider answering us correctly about a bad request. That is our bug,
    /// not the integration being down, so it must not move readiness.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Handler_ignores_ordinary_client_errors(HttpStatusCode status)
    {
        await SendThroughHandler(status, "https://api.paymongo.com/v1/checkout_sessions");

        var snapshot = _state.GetSnapshot(PaymentProviders.PayMongo);
        Assert.False(snapshot.CredentialsRejected);
        Assert.Null(snapshot.LastFailureUtc);
    }

    [Fact]
    public async Task Handler_records_a_server_error_as_a_failure()
    {
        await SendThroughHandler(HttpStatusCode.BadGateway, "https://api.paymongo.com/v1/checkout_sessions");

        Assert.NotNull(_state.GetSnapshot(PaymentProviders.PayMongo).LastFailureUtc);
    }

    [Fact]
    public async Task Handler_records_a_transport_exception_and_rethrows()
    {
        var handler = new PaymentGatewayTelemetryHandler(PaymentProviders.PayMongo, _state)
        {
            InnerHandler = new ThrowingHandler(new HttpRequestException("connection refused"))
        };
        using var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://api.paymongo.com/v1/refunds"), CancellationToken.None));

        var snapshot = _state.GetSnapshot(PaymentProviders.PayMongo);
        Assert.NotNull(snapshot.LastFailureUtc);
        Assert.Contains("connection refused", snapshot.LastFailureReason);
    }

    // ---- Operation naming --------------------------------------------------------------------

    /// <summary>
    /// Resource ids must never reach a metric tag - one time series per payment is an unbounded
    /// cardinality explosion, which is exactly why the built-in HttpClient instrumentation declines
    /// to tag the path at all.
    /// </summary>
    [Theory]
    [InlineData("POST", "https://api.paymongo.com/v1/checkout_sessions", "POST /checkout_sessions")]
    [InlineData("GET", "https://api.paymongo.com/v1/checkout_sessions/cs_abc123", "GET /checkout_sessions")]
    [InlineData("POST", "https://api.paymongo.com/v1/refunds", "POST /refunds")]
    [InlineData("GET", "https://api.xendit.co/v2/invoices/inv_999", "GET /invoices")]
    [InlineData("POST", "https://api.xendit.co/transfers", "POST /transfers")]
    [InlineData("GET", "https://api.paymongo.com/", "GET")]
    public void Operation_name_is_bounded_and_id_free(string method, string url, string expected)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);

        Assert.Equal(expected, PaymentGatewayTelemetryHandler.DescribeOperation(request));
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw exception;
    }
}
