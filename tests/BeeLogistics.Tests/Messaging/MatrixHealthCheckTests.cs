using System.Diagnostics;
using System.Net;
using BeeLogistics.Modules.Messaging.Application;
using BeeLogistics.Modules.Messaging.Infrastructure;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Messaging;

/// <summary>
/// The contract that keeps a chat outage from becoming an API outage.
/// </summary>
/// <remarks>
/// This check is tagged "ready", so whatever it returns lands on <c>/health/ready</c> — the probe
/// an orchestrator uses to decide whether this instance should receive traffic. Booking chat is not
/// on the critical path for creating or completing a delivery, so the rule is: report the problem,
/// never fail the probe, and never take long about it.
/// </remarks>
public class MatrixHealthCheckTests
{
    private static MatrixHealthCheck Check(HttpMessageHandler handler, bool enabled = true)
    {
        var options = new MatrixOptions
        {
            Enabled = enabled,
            HomeserverUrl = "http://beeapp-synapse:8008",
            ServerName = "mybeeapp.com",
            AsToken = "as",
            HsToken = "hs",
        };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(MatrixHttpClient.Name).Returns(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = MatrixHttpClient.NormalizeBaseAddress(options.HomeserverUrl),
        });

        return new MatrixHealthCheck(factory, Options.Create(options));
    }

    private static Task<HealthCheckResult> RunAsync(MatrixHealthCheck check, CancellationToken ct = default) =>
        check.CheckHealthAsync(new HealthCheckContext(), ct);

    [Fact]
    public async Task Disabled_is_healthy_without_touching_the_network()
    {
        var handler = new ThrowingHandler();

        var result = await RunAsync(Check(handler, enabled: false));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task Reachable_homeserver_is_healthy()
    {
        using var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, """{"versions":["v1.13"]}""");

        var result = await RunAsync(Check(handler));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("http://beeapp-synapse:8008/_matrix/client/versions", handler.Request!.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task An_unhappy_homeserver_is_Degraded_never_Unhealthy(HttpStatusCode statusCode)
    {
        using var handler = new CapturingHttpMessageHandler(statusCode, "{}");

        var result = await RunAsync(Check(handler));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task An_unreachable_homeserver_is_Degraded_never_Unhealthy()
    {
        // The case that matters most: Synapse is down entirely. If this ever returns Unhealthy,
        // every API instance drops out of rotation because the chat server fell over.
        var result = await RunAsync(Check(new ThrowingHandler()));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task A_hanging_homeserver_is_Degraded_rather_than_hanging_the_probe()
    {
        // Would otherwise sit for the client's full 45s retry budget and make a chat outage look
        // like an API hang.
        using var handler = new HangingHandler();

        var result = await RunAsync(Check(handler));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("did not respond", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_cancellation_propagates_instead_of_being_reported_as_a_Synapse_fault()
    {
        using var handler = new HangingHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Check(handler), cts.Token));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new HttpRequestException("Connection refused");
        }
    }

    /// <summary>Never responds, so only the probe's own timeout can end the call.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
