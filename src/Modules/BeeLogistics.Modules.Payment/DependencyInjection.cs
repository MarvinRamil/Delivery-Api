using System.Text;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Infrastructure;
using BeeLogistics.Modules.Payment.Infrastructure.Repositories;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment;

public static class DependencyInjection
{
    public static IMvcBuilder AddPaymentModule(this IMvcBuilder mvcBuilder, string connectionString, IConfiguration configuration)
    {
        var services = mvcBuilder.Services;

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(connectionString, x => x.MigrationsHistoryTable("__PaymentMigrationsHistory", "public")));

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<ISavedPaymentMethodRepository, SavedPaymentMethodRepository>();
        services.AddScoped<IPaymentAccessPolicy, Application.Services.PaymentAccessPolicy>();

        services.AddOptions<XenditOptions>()
            .Bind(configuration.GetSection(XenditOptions.SectionName));
        services.AddOptions<PayMongoOptions>()
            .Bind(configuration.GetSection(PayMongoOptions.SectionName));
        services.AddOptions<PaymentGatewayOptions>()
            .Bind(configuration.GetSection(PaymentGatewayOptions.SectionName));

        // Singleton: this is process-wide state describing what real traffic has told us about
        // each provider, read by the readiness probe. Registered before the clients that feed it.
        services.AddSingleton<Application.Services.IPaymentGatewayHealthState, PaymentGatewayHealthState>();

        services.AddHttpClient<XenditGateway>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<XenditOptions>>().Value;
                if (!opts.IsConfigured)
                    throw new InvalidOperationException("Xendit:ApiKey not configured");
                client.BaseAddress = new Uri(opts.BaseUrl);
                // Basic auth: API key as username, empty password. Built here so the
                // adapter never holds the key in a field (keeps it out of dumps/exceptions).
                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{opts.ApiKey}:"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
            })
            // Registered before the resilience handler so it sits *outside* it. Handlers run
            // outermost-first, so this records one entry per logical call - final outcome, retries
            // already spent, an open circuit counted as the failure the caller saw. Registered the
            // other way round it would time each individual attempt, which is what
            // AddHttpClientInstrumentation already does.
            .AddHttpMessageHandler(sp => new Infrastructure.Http.PaymentGatewayTelemetryHandler(
                PaymentProviders.Xendit,
                sp.GetRequiredService<Application.Services.IPaymentGatewayHealthState>()))
            // Timeouts, retry with backoff, and circuit breaker for Xendit calls.
            // Retries are limited to idempotent methods (GET/DELETE etc.) so a POST
            // without an idempotency key can never create duplicate invoices.
            .AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                options.Retry.DisableForUnsafeHttpMethods();
                ReportCircuitState(options, PaymentProviders.Xendit, sp);
            });

        services.AddHttpClient<PayMongoGateway>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<PayMongoOptions>>().Value;
                if (!opts.IsConfigured)
                    throw new InvalidOperationException("PayMongo:SecretKey not configured");
                client.BaseAddress = new Uri(opts.BaseUrl);
                // Basic auth: secret key as username, empty password (same scheme as Xendit)
                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{opts.SecretKey}:"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
            })
            // Outside the resilience handler - see the Xendit registration above.
            .AddHttpMessageHandler(sp => new Infrastructure.Http.PaymentGatewayTelemetryHandler(
                PaymentProviders.PayMongo,
                sp.GetRequiredService<Application.Services.IPaymentGatewayHealthState>()))
            .AddStandardResilienceHandler()
            .Configure((options, sp) =>
            {
                // PayMongo POSTs (refunds, transfers) carry no idempotency-key header,
                // so retries stay limited to safe methods to avoid duplicate money movement.
                options.Retry.DisableForUnsafeHttpMethods();
                ReportCircuitState(options, PaymentProviders.PayMongo, sp);
            });

        // Child-account onboarding (PayMongo Platforms). Its own typed client rather than a method
        // on PayMongoGateway: this is a capability no other provider has, and the accounts API is
        // versioned and shaped differently from the payment routes.
        services.AddHttpClient<IPayMongoAccountsClient, PayMongoAccountsClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<PayMongoOptions>>().Value;
                if (!opts.IsConfigured)
                    throw new InvalidOperationException("PayMongo:SecretKey not configured");
                client.BaseAddress = new Uri(opts.BaseUrl);
                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{opts.SecretKey}:"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
            })
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                // Account creation and activation are not idempotent: a retried create opens a
                // second child account, and activation is irreversible. Retries stay on safe methods.
                options.Retry.DisableForUnsafeHttpMethods();
            });

        // Strategy/adapter wiring: each CONFIGURED provider registers as an
        // IPaymentGateway; the factory picks by Payments:ActiveGateway for new
        // records and by the record's stored Provider for existing ones.
        // Unconfigured providers are simply absent — the factory then throws a
        // clear "configure its credentials" error instead of failing DI resolution.
        if (!string.IsNullOrWhiteSpace(configuration["Xendit:ApiKey"]))
            services.AddScoped<IPaymentGateway>(sp => sp.GetRequiredService<XenditGateway>());
        if (!string.IsNullOrWhiteSpace(configuration["PayMongo:SecretKey"]))
            services.AddScoped<IPaymentGateway>(sp => sp.GetRequiredService<PayMongoGateway>());

        services.AddScoped<IPaymentGatewayFactory, PaymentGatewayFactory>();
        services.AddScoped<Application.Services.IAbandonedCheckoutExpirer, AbandonedCheckoutExpirer>();
        services.AddScoped<Application.Services.ICancelledBookingPaymentAuditor, CancelledBookingPaymentAuditor>();
        services.AddScoped<Application.Services.IStuckRefundResolver, StuckRefundResolver>();
        services.AddScoped<IPayMongoWebhookSignatureVerifier, PayMongoWebhookSignatureVerifier>();
        services.AddScoped<Presentation.Webhooks.WebhookEventStore>();

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        mvcBuilder.AddApplicationPart(typeof(DependencyInjection).Assembly);

        return mvcBuilder;
    }

    /// <summary>
    /// Mirrors the resilience pipeline's circuit-breaker transitions into
    /// <see cref="Application.Services.IPaymentGatewayHealthState"/>, so the readiness probe can
    /// report on the provider without ever calling it (GitLab #35).
    ///
    /// The breaker is the right signal because it is driven by the traffic that actually matters,
    /// not by a synthetic probe on the health path. Its blind spot is authentication: a revoked key
    /// returns 401/403, which the standard handler treats as a valid answer rather than a fault, so
    /// the breaker stays closed. <see cref="Infrastructure.Http.PaymentGatewayTelemetryHandler"/>
    /// covers that half.
    /// </summary>
    private static void ReportCircuitState(
        HttpStandardResilienceOptions options,
        string provider,
        IServiceProvider serviceProvider)
    {
        var state = serviceProvider.GetRequiredService<Application.Services.IPaymentGatewayHealthState>();

        options.CircuitBreaker.OnOpened = _ =>
        {
            state.RecordCircuitState(provider, Application.Services.GatewayCircuitState.Open, DateTime.UtcNow);
            return default;
        };

        options.CircuitBreaker.OnClosed = _ =>
        {
            state.RecordCircuitState(provider, Application.Services.GatewayCircuitState.Closed, DateTime.UtcNow);
            return default;
        };

        options.CircuitBreaker.OnHalfOpened = _ =>
        {
            state.RecordCircuitState(provider, Application.Services.GatewayCircuitState.HalfOpen, DateTime.UtcNow);
            return default;
        };
    }
}
