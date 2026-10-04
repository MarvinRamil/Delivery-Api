using System.Net;
using BeeLogistics.Modules.Payment;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Exercises the real <see cref="DependencyInjection.AddPaymentModule"/> registration rather than a
/// hand-rolled copy of it (GitLab #35).
///
/// The wiring is the risky part of this change and it is not something a compile proves: the
/// resilience pipeline's <c>Configure((options, serviceProvider) =&gt; ...)</c> callback runs when
/// the pipeline is first built, not at startup, so a bad service resolution there would surface on
/// the first real gateway call rather than at boot. Booting the app in CI is not an option either -
/// it fails on Vault configuration long before the container is built.
/// </summary>
public class PaymentGatewayWiringTests
{
    private static ServiceProvider BuildProvider(string activeGateway = PaymentProviders.PayMongo)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PayMongo:SecretKey"] = "sk_test_wiring",
                ["PayMongo:BaseUrl"] = "https://api.paymongo.com/v1/",
                ["Xendit:ApiKey"] = "xnd_test_wiring",
                ["Xendit:BaseUrl"] = "https://api.xendit.co/",
                ["Payments:ActiveGateway"] = activeGateway
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddPaymentModule("Host=localhost;Database=none", configuration);

        // Swap the network out from under the real pipeline. Everything registered above it -
        // telemetry handler, resilience handler - still runs exactly as AddPaymentModule wired it.
        services.AddHttpClient<PayMongoGateway>()
            .ConfigurePrimaryHttpMessageHandler(() => new UnreachableTransport());

        // ValidateScopes on purpose: a singleton capturing a scoped service is exactly the kind of
        // wiring mistake this test exists to catch.
        //
        // ValidateOnBuild is deliberately off. It walks every descriptor in the collection, and
        // AddControllers registers MVC infrastructure that cannot resolve outside a web host
        // (IWebHostEnvironment and friends). Turning it on fails on ASP.NET's own services and tells
        // us nothing about the payment module. The assertions below resolve what actually matters.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        });
    }

    [Fact]
    public void Health_state_resolves_from_the_real_registration()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IPaymentGatewayHealthState>());
    }

    [Fact]
    public void Health_state_is_a_singleton_shared_across_scopes()
    {
        using var provider = BuildProvider();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        Assert.Same(
            scopeA.ServiceProvider.GetRequiredService<IPaymentGatewayHealthState>(),
            scopeB.ServiceProvider.GetRequiredService<IPaymentGatewayHealthState>());
    }

    /// <summary>
    /// The health check is registered by name in Program.cs and constructed by the health-check
    /// infrastructure, so nothing else would catch its constructor drifting from what DI can supply.
    /// </summary>
    [Fact]
    public async Task Health_check_can_be_constructed_from_the_container()
    {
        using var provider = BuildProvider();

        var check = ActivatorUtilities.CreateInstance<PaymentGatewayHealthCheck>(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// Builds the typed client's real handler pipeline - including the resilience handler whose
    /// Configure callback resolves the health state - and drives one call through it. This is the
    /// assertion that the <c>(options, serviceProvider)</c> overload actually resolves at pipeline
    /// build time instead of throwing on the first checkout.
    /// </summary>
    [Fact]
    public async Task Telemetry_handler_is_wired_into_the_typed_client_pipeline()
    {
        using var provider = BuildProvider();
        var state = provider.GetRequiredService<IPaymentGatewayHealthState>();

        Assert.Equal(GatewayCircuitState.Unknown, state.GetSnapshot(PaymentProviders.PayMongo).Circuit);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PayMongoGateway));

        // POST on purpose: DisableForUnsafeHttpMethods() means it is not retried, so this fails
        // immediately instead of walking the retry backoff.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.PostAsync("checkout_sessions", new StringContent("{}"), CancellationToken.None));

        var snapshot = state.GetSnapshot(PaymentProviders.PayMongo);
        Assert.NotNull(snapshot.LastFailureUtc);
        Assert.Contains("unreachable", snapshot.LastFailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The telemetry handler is registered before the resilience handler so it sits outside it,
    /// recording one entry per logical call rather than one per retry attempt. Registered the other
    /// way round it would duplicate what AddHttpClientInstrumentation already emits, and a single
    /// flaky call would look like several.
    ///
    /// A GET is retried (DisableForUnsafeHttpMethods only spares unsafe verbs), so the transport
    /// sees several attempts while the health state must be told exactly once.
    /// </summary>
    [Fact]
    public async Task Telemetry_records_once_per_logical_call_not_once_per_retry()
    {
        var transport = new CountingUnreachableTransport();
        var spy = new CountingHealthState();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PayMongo:SecretKey"] = "sk_test_wiring",
                ["PayMongo:BaseUrl"] = "https://api.paymongo.com/v1/",
                ["Payments:ActiveGateway"] = PaymentProviders.PayMongo
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddPaymentModule("Host=localhost;Database=none", configuration);
        services.AddHttpClient<PayMongoGateway>()
            .ConfigurePrimaryHttpMessageHandler(() => transport)
            // Collapse the retry backoff. The assertion is about *where* the telemetry handler sits
            // in the pipeline, not how long the waits are, and the real delays cost seven seconds
            // in a suite that otherwise runs in well under one.
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                options.Retry.Delay = TimeSpan.Zero;
                options.Retry.UseJitter = false;
            });
        // Last registration wins, so the pipeline reports into the spy.
        services.AddSingleton<IPaymentGatewayHealthState>(spy);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PayMongoGateway));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.GetAsync("checkout_sessions/cs_probe", CancellationToken.None));

        Assert.True(transport.Attempts > 1, $"expected the resilience handler to retry a GET, saw {transport.Attempts} attempt(s)");
        Assert.Equal(1, spy.Failures);
    }

    /// <summary>
    /// Stands in for the network at the bottom of the real handler chain.
    /// </summary>
    private sealed class UnreachableTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated unreachable provider");
    }

    private sealed class CountingUnreachableTransport : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new HttpRequestException("simulated unreachable provider");
        }
    }

    private sealed class CountingHealthState : IPaymentGatewayHealthState
    {
        private readonly PaymentGatewayHealthState _inner = new();
        private int _failures;
        public int Failures => Volatile.Read(ref _failures);

        public void RecordSuccess(string provider, DateTime nowUtc) => _inner.RecordSuccess(provider, nowUtc);

        public void RecordCredentialsRejected(string provider, int statusCode, DateTime nowUtc)
            => _inner.RecordCredentialsRejected(provider, statusCode, nowUtc);

        public void RecordFailure(string provider, string reason, DateTime nowUtc)
        {
            Interlocked.Increment(ref _failures);
            _inner.RecordFailure(provider, reason, nowUtc);
        }

        public void RecordCircuitState(string provider, GatewayCircuitState state, DateTime nowUtc)
            => _inner.RecordCircuitState(provider, state, nowUtc);

        public PaymentGatewayHealthSnapshot GetSnapshot(string provider) => _inner.GetSnapshot(provider);
    }
}
