using BeeLogistics.Api;
using BeeLogistics.Api.Extensions;
using BeeLogistics.Api.Filters;
using BeeLogistics.Api.Middleware;
using Scalar.AspNetCore;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Behaviors;
using BeeLogistics.Shared.Infrastructure;
using BeeLogistics.Shared.Infrastructure.Security;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Identity;
using BeeLogistics.Modules.Identity.Infrastructure;
using BeeLogistics.Modules.Bookings;
using BeeLogistics.Modules.Bookings.Infrastructure;
using BeeLogistics.Modules.Bookings.Infrastructure.Services;
using BeeLogistics.Modules.Payment;
using BeeLogistics.Modules.Payment.Infrastructure;
using BeeLogistics.Modules.Notification;
using BeeLogistics.Modules.Chat;
using BeeLogistics.Modules.Chat.Infrastructure;
using BeeLogistics.Modules.Chat.Presentation.Hubs;
using BeeLogistics.Modules.CRM;
using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Modules.Drivers;
using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Consumers;
using BeeLogistics.Modules.Drivers.Infrastructure;
using BeeLogistics.Modules.Referrals;
using BeeLogistics.Modules.Giveaways;
using BeeLogistics.Modules.Integration;
using BeeLogistics.Modules.Messaging;
using BeeLogistics.Modules.Offers;
using BeeLogistics.Modules.Fraud;
using BeeLogistics.Modules.Accounting;
using BeeLogistics.Modules.Verification;
using BeeLogistics.Modules.Map;
using BeeLogistics.Modules.Map.Presentation.Hubs;
using BeeLogistics.Modules.Rating;
using BeeLogistics.Modules.Revenue;
using BeeLogistics.Modules.Revenue.Infrastructure;
using Minio;
using BeeLogistics.Shared.Hubs;
using BeeLogistics.Api.Services;
using BeeLogistics.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Hangfire;
using Hangfire.Storage;
using Hangfire.PostgreSql;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Microsoft.Extensions.Logging;
using BeeLogistics.Shared.Infrastructure.Configuration;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MassTransit;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using BeeLogistics.Api.Observability;
using Sentry.Extensibility;
using Sentry.Serilog;
using Serilog.Events;

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

// Build config for Serilog (same sources as app)
var logConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// Configure Serilog: Console + File + Seq (URL from SEQ_SERVER_URL env; empty = disabled)
var seqServerUrl = logConfig["SEQ_SERVER_URL"] ?? logConfig["Serilog__SeqServerUrl"] ?? logConfig["Seq:ServerUrl"] ?? "";

// Error-monitoring DSN (env: Sentry__Dsn, or Sentry__Dsn in the Vault config secret). The backend
// now reports to self-hosted GlitchTip, which speaks the Sentry protocol, so the Sentry.* packages
// and every Sentry__/Sentry: configuration key below are unchanged - only the endpoint moved.
// Empty/missing => error monitoring is fully off: no SDK init, no sink, no egress.
//
// This has to be resolved HERE, before the logger is created, because the Sentry Serilog sink below
// is the real capture path and a sink can only be attached at logger-construction time. That is
// earlier than builder.Configuration and its Vault overlay exist, so the Vault lookup is repeated
// explicitly. Environment/appsettings wins so a deploy can override Vault without a Vault write.
var sentryDsn = (logConfig["Sentry:Dsn"] ?? "").Trim();

// Development is excluded from the Vault fallback on purpose. Vault holds the shared production
// DSN, and every developer machine can reach Vault, so falling back there would pipe every local
// exception into the live GlitchTip project and drown real production alerts. A developer who wants
// error monitoring locally sets Sentry__Dsn explicitly, which is handled by the read above.
if (string.IsNullOrWhiteSpace(sentryDsn) &&
    !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
{
    // Best-effort and deliberately silent on failure: the authoritative Vault load further down
    // (builder.Configuration.AddVaultSecrets) is the one that fails fast and reports properly, and
    // Log.Logger does not exist yet to report anything here. If Vault is genuinely unreachable the
    // app will not boot regardless; the only consequence of losing this read is Sentry staying off.
    try
    {
        var bootstrapVaultOptions = logConfig
            .GetSection(BeeLogistics.Shared.Infrastructure.Security.VaultOptions.SectionName)
            .Get<BeeLogistics.Shared.Infrastructure.Security.VaultOptions>();

        if (bootstrapVaultOptions is not null)
        {
            sentryDsn = (new ConfigurationBuilder()
                .AddVaultSecrets(bootstrapVaultOptions, bootstrapVaultOptions.ConfigSecretPath)
                .Build()["Sentry:Dsn"] ?? "").Trim();
        }
    }
    catch
    {
        // Swallowed by design — see above.
    }
}

var loggerConfig = new LoggerConfiguration()
    // Sentry's ASP.NET Core integration logs "Sentry trace header is null. Creating new Sentry
    // Propagation Context." at Information on every incoming request. With EnableLogs on (see
    // UseSentry below) that noise would ship to GlitchTip as a log line per request - roughly
    // 2,900/day from the 30s health probes alone, for zero diagnostic value. This lives here and
    // not in appsettings.json because that file is gitignored and absent from the deployed image.
    // ReadFrom.Configuration runs after this, so Serilog__MinimumLevel__Override__Sentry still wins
    // if an environment needs it turned back up.
    .MinimumLevel.Override("Sentry", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7);

if (!string.IsNullOrWhiteSpace(seqServerUrl))
{
    loggerConfig = loggerConfig.WriteTo.Seq(seqServerUrl.Trim());
}

// Sentry sink. This overload takes NO dsn argument, so Sentry.Serilog infers InitializeSdk = false
// and the sink writes into the ambient SentrySdk hub that builder.WebHost.UseSentry() creates below
// — exactly one SDK init. This sink, not Sentry's ASP.NET Core middleware, is the real capture path:
// ExceptionHandlingMiddleware never rethrows, so nothing ever reaches that middleware. It is also
// what covers Hangfire jobs, MassTransit consumers and hosted services.
//
// Argument order here is (minimumEventLevel, minimumBreadcrumbLevel) — the reverse of the dsn
// overload — hence the named arguments. Do NOT add a "Sentry" entry to Serilog:WriteTo in
// appsettings.json: that path takes a dsn and would initialize a second SDK.
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    loggerConfig = loggerConfig.WriteTo.Sentry(
        minimumEventLevel: LogEventLevel.Error,             // Error/Fatal -> GlitchTip issue
        minimumBreadcrumbLevel: LogEventLevel.Information); // Info/Warning -> breadcrumb only
}

loggerConfig = loggerConfig.ReadFrom.Configuration(logConfig);

Log.Logger = loggerConfig.CreateLogger();

try
{
    Log.Information("Starting BeeLogistics API");

var builder = WebApplication.CreateBuilder(args);

// --- GLITCHTIP ERROR MONITORING (Sentry SDK / Sentry wire protocol) ---
// The one and only SDK initialization. Guarded on a non-empty DSN so local dev and CI never register
// the middleware or open a socket. UseSentry binds Configuration:Sentry before this callback runs,
// so this callback is the source of truth for everything it sets.
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    builder.WebHost.UseSentry(o =>
    {
        // Must never be null: SettingLocator.GetDsn() throws when Dsn is null and SENTRY_DSN is unset.
        o.Dsn = sentryDsn;

        // Environment is derived from ASPNETCORE_ENVIRONMENT by SetEnvironment() and lowercased for
        // the standard names, so it is deliberately not set here.
        o.Release = Environment.GetEnvironmentVariable("SENTRY_RELEASE") is { Length: > 0 } release
            ? $"bee-backend@{release}"             // CI passes CI_COMMIT_SHORT_SHA
            : null;
        o.ServerName = "bee-backend";              // same service name as the OpenTelemetry resource
        o.Debug = builder.Environment.IsDevelopment();
        o.AttachStacktrace = true;
        o.AutoSessionTracking = false;             // release-health sessions are pure quota cost here

        // Structured logs. This one flag is the whole feature: with it set, the Serilog sink below
        // additionally ships every log event the logger lets through as a GlitchTip *log* (a
        // separate "type":"log" envelope, origin auto.log.serilog), independently of the
        // minimumEventLevel that governs issues. So Error/Fatal still become issues AND appear in
        // the log stream, while Information/Warning become log lines only.
        //
        // The threshold for logs is Serilog's own MinimumLevel, not anything set here - there is no
        // per-log-level knob on the sink. Turning Serilog's level up therefore turns GlitchTip
        // ingest volume up with it. Note appsettings.json is gitignored and absent from the
        // deployed image, so level overrides that need to reach a deployment go in .gitlab-ci.yml
        // as Serilog__MinimumLevel__Override__* environment variables, not in a settings file.
        o.EnableLogs = true;

        // --- Philippine Data Privacy Act (RA 10173) ---
        o.SendDefaultPii = false;
        o.MaxRequestBodySize = RequestSize.None;   // /api/webhooks buffers payment and KYC bodies
        o.SetBeforeSend(SentryScrubbing.BeforeSend);
        o.SetBeforeBreadcrumb(SentryScrubbing.BeforeBreadcrumb);

        // The Serilog sink is not initializing the SDK, so its scope processor has to be registered
        // here or Serilog LogContext properties are dropped from Sentry events.
        o.ApplySerilogScopeToEvents();

        // 499 client-cancels are never logged, but MassTransit and Hangfire log
        // TaskCanceledException at Error during shutdown.
        o.AddExceptionFilterForType<OperationCanceledException>();

        foreach (var pattern in SentryScrubbing.IgnoredTransactionPatterns)
        {
            o.IgnoreTransactions.Add(pattern);
        }

        // Tracing stays off: OpenTelemetry already owns tracing (gated on OpenTelemetry:OtlpEndpoint),
        // and GlitchTip's performance support is thin compared to its error tracking. GlitchTip's
        // onboarding snippet suggests TracesSampleRate = 0.01; that is deliberately not adopted here.
        // Error capture is the whole job.
        o.TracesSampleRate = 0.0;
    });
}
else
{
    Log.Warning("GlitchTip error monitoring is DISABLED (Sentry:Dsn is empty). Set Sentry__Dsn in Vault (bee/config) or as an environment variable to enable.");
}

// Make appsettings.json optional (for Docker deployment without config file)
// All configuration should come from environment variables
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// --- VAULT: single source of truth for application secrets ---
// Bootstrap Vault from the config loaded so far (Address + RoleId/SecretId via appsettings/env),
// then add the Vault secret as the HIGHEST-precedence source so it overrides file/env defaults.
// Fails fast at startup if Vault is unreachable or the secret is missing.
var vaultOptions = builder.Configuration.GetSection(BeeLogistics.Shared.Infrastructure.Security.VaultOptions.SectionName)
    .Get<BeeLogistics.Shared.Infrastructure.Security.VaultOptions>()
    ?? throw new InvalidOperationException("Missing 'Vault' configuration section.");
try
{
    builder.Configuration.AddVaultSecrets(vaultOptions, vaultOptions.ConfigSecretPath);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Failed to load application secrets from Vault at {Address} ({Mount}/{Path}). The application cannot start.",
        vaultOptions.Address, vaultOptions.MountPath, vaultOptions.ConfigSecretPath);
    throw;
}

try
{
    builder.Configuration.AddVaultSecrets(vaultOptions, vaultOptions.DriverConfigSecretPath);
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to load driver config from Vault at {Address} ({Mount}/{Path}). /api/config will return empty values.",
        vaultOptions.Address, vaultOptions.MountPath, vaultOptions.DriverConfigSecretPath);
}

// --- DEV OVERRIDE: local appsettings win over Vault for selected sections ---
// In Development (e.g. `dotnet run` / `dotnet watch`), let local config win over the Vault
// overlay so you can use sandbox keys without touching Vault. ONLY the sections listed below
// are overridden — DB, RabbitMQ, encryption keys, etc. still come from Vault. Reads ONLY the
// developer's local overrides (appsettings.Development.json + env), never the shared base
// appsettings.json, and is a no-op for any section not actually defined there.
//
// Blank values are skipped, so this cannot blank out a Vault secret. The deploy scripts pass
// storage keys unconditionally with an empty default (`-e "DigitalOcean__Endpoint=${VAR:-}"`),
// which the environment source surfaces as present-but-empty; those used to be re-applied over
// Vault here and took file storage down to NoOp on the dev container. (#42)
if (builder.Environment.IsDevelopment())
{
    var devConfig = new ConfigurationBuilder()
        .SetBasePath(builder.Environment.ContentRootPath)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    // Section names map to their *Options.SectionName:
    //   Payments: "PayMongo", "Payment"
    //   KYC / driver verification: "Didit" (KYC), "FaceMatch", "ShiftCheck"
    //   Push / FCM: "Firebase" (service account), "Push" (provider override), "Expo" (access token)
    //   File storage: "FileStorage" (active provider), "S3" / "DigitalOcean" (provider profiles)
    var devOverrideSections = new[]
    {
        "PayMongo", "Payment",
        "Didit", "FaceMatch", "ShiftCheck",
        "Firebase", "Push", "Expo",
        "FileStorage", "S3", "DigitalOcean",
    };

    var devOverrides = BeeLogistics.Shared.Infrastructure.Configuration.DevelopmentConfigOverrides
        .Collect(devConfig, devOverrideSections);

    if (devOverrides.Count > 0)
    {
        builder.Configuration.AddInMemoryCollection(devOverrides);
        Log.Information("Development: {Count} config keys sourced from appsettings (overriding Vault): {Sections}.",
            devOverrides.Count, string.Join(", ", devOverrideSections));
    }
}

// OWASP: Disable server identification headers at server level
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false; // Disable Server header in Kestrel
});

builder.Host.UseSerilog();

// --- DATABASE CONNECTION ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    Log.Fatal("DefaultConnection connection string is required but not configured. " +
              "Please set ConnectionStrings__DefaultConnection environment variable.");
    throw new InvalidOperationException(
        "DefaultConnection connection string is required. " +
        "Set ConnectionStrings__DefaultConnection environment variable.");
}

// --- PRODUCTION CONFIG FAIL-FAST ---
// A missing env var must crash startup, not silently fall back to localhost/guest
// defaults that leave the system quietly broken.
if (builder.Environment.IsProduction())
{
    var missing = new List<string>();

    foreach (var key in new[] { "RabbitMq:Host", "RabbitMq:Username", "RabbitMq:Password" })
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[key]))
            missing.Add(key);
    }

    // Also protects the Map module's IConnectionMultiplexer, which resolves
    // Redis:ConnectionString ?? ConnectionStrings:Redis (see AddMapModule).
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
        missing.Add("ConnectionStrings:Redis");

    if (builder.Configuration["RabbitMq:Username"] == "guest")
        missing.Add("RabbitMq:Username (must not be 'guest' in production)");

    // --- Payment gateway configuration ---
    // The active gateway routes NEW payments; existing records route by their stored
    // Provider, so any CONFIGURED gateway must also have webhook auth — a configured
    // provider with an open webhook endpoint would let anyone mark payments paid.
    var activeGateway = builder.Configuration["Payments:ActiveGateway"] ?? "paymongo";
    var xenditConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Xendit:ApiKey"]);
    var payMongoConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["PayMongo:SecretKey"]);

    if (!activeGateway.Equals("xendit", StringComparison.OrdinalIgnoreCase) &&
        !activeGateway.Equals("paymongo", StringComparison.OrdinalIgnoreCase))
        missing.Add($"Payments:ActiveGateway (must be 'xendit' or 'paymongo', got '{activeGateway}')");

    if (activeGateway.Equals("xendit", StringComparison.OrdinalIgnoreCase) && !xenditConfigured)
        missing.Add("Xendit:ApiKey (required: Xendit is the active gateway)");
    if (activeGateway.Equals("paymongo", StringComparison.OrdinalIgnoreCase) && !payMongoConfigured)
        missing.Add("PayMongo:SecretKey (required: PayMongo is the active gateway)");

    if (xenditConfigured)
    {
        var hasXenditWebhookAuth =
            !string.IsNullOrWhiteSpace(builder.Configuration["Xendit:WebhookToken"]) ||
            (!string.IsNullOrWhiteSpace(builder.Configuration["Xendit:PublicKey"]) && builder.Configuration["Xendit:PublicKey"] != "REPLACE_WITH_XENDIT_PUBLIC_KEY") ||
            (builder.Configuration.GetSection("Xendit:AllowedSourceIps").Get<string[]>()?.Length > 0);
        if (!hasXenditWebhookAuth)
            missing.Add("Xendit webhook auth (one of: Xendit:WebhookToken, Xendit:PublicKey, Xendit:AllowedSourceIps)");
    }
    else
    {
        Log.Warning("Xendit is not configured (no Xendit:ApiKey). In-flight Xendit payments cannot be refunded or reconciled.");
    }

    if (payMongoConfigured)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["PayMongo:WebhookSecret"]))
            missing.Add("PayMongo:WebhookSecret (required whenever PayMongo:SecretKey is configured)");

        // A whsk_ in the secret-key slot 401s every PayMongo call (see the all-environment
        // check below, which is what actually surfaces this during development).
        if (!PayMongoKeyLooksLikeSecretKey(builder.Configuration["PayMongo:SecretKey"]))
            missing.Add("PayMongo:SecretKey must be a secret API key (sk_test_… / sk_live_…)");
    }
    else
    {
        Log.Warning("PayMongo is not configured (no PayMongo:SecretKey). In-flight PayMongo payments cannot be refunded or reconciled.");
    }

    // --- Matrix appservice configuration ---
    // Only enforced when Matrix:Enabled is true. A half-configured appservice is worse than a
    // disabled one: rooms silently fail to provision and the failure only surfaces as bookings
    // with no chat, hours later. MatrixOptions.Validate() is the single source of truth so the
    // module and this guard cannot drift.
    var matrixOptions = new BeeLogistics.Modules.Messaging.Application.MatrixOptions();
    builder.Configuration
        .GetSection(BeeLogistics.Modules.Messaging.Application.MatrixOptions.SectionName)
        .Bind(matrixOptions);
    missing.AddRange(matrixOptions.Validate());

    if (!matrixOptions.Enabled)
        Log.Warning("Matrix booking chat is disabled (Matrix:Enabled=false). Bookings will not get chat rooms.");

    if (missing.Count > 0)
        throw new InvalidOperationException(
            "Refusing to start in Production with missing/unsafe configuration: " + string.Join("; ", missing));
}

// --- PAYOUT PREFLIGHT (all environments) ---
// The fail-fast block above only runs in Production, but a misconfigured payout path is
// exactly what breaks development: driver withdrawals failed for a long time with nothing
// in the logs but "temporarily unavailable". These are loud but non-fatal, because a
// checkout-only deployment with payouts switched off is legitimate.
{
    var activePaymentGateway = builder.Configuration["Payments:ActiveGateway"] ?? "paymongo";
    if (activePaymentGateway.Equals("paymongo", StringComparison.OrdinalIgnoreCase))
    {
        var payMongoKey = builder.Configuration["PayMongo:SecretKey"];

        if (!string.IsNullOrWhiteSpace(payMongoKey) && !PayMongoKeyLooksLikeSecretKey(payMongoKey))
        {
            Log.Error(
                "PayMongo:SecretKey is '{Prefix}…', not a secret API key. Secret keys start with "
                + "sk_test_ or sk_live_; a whsk_… value is the WEBHOOK SIGNING SECRET and authenticates "
                + "nothing — every PayMongo call will return 401 unauthorized / 'failed to get organization'. "
                + "Check PayMongo:SecretKey and PayMongo:WebhookSecret are not swapped.",
                payMongoKey.Trim()[..Math.Min(5, payMongoKey.Trim().Length)]);
        }

        if (string.IsNullOrWhiteSpace(builder.Configuration["PayMongo:SourceAccountNumber"]) ||
            string.IsNullOrWhiteSpace(builder.Configuration["PayMongo:SourceAccountName"]) ||
            string.IsNullOrWhiteSpace(builder.Configuration["PayMongo:SourceAccountBic"]))
        {
            Log.Error(
                "PayMongo disbursements are DISABLED: PayMongo:SourceAccountNumber / SourceAccountName / "
                + "SourceAccountBic are not all set, so every driver withdrawal will fail before reaching "
                + "PayMongo. Read the three values from your wallet: "
                + "curl -sL https://api.paymongo.com/v2/wallets/ -u \"$PAYMONGO_SECRET_KEY:\"");
        }
    }
}

// Secret API keys are sk_test_/sk_live_; whsk_ is the webhook signing secret and is not
// interchangeable with one, though both are copied from the same dashboard page.
static bool PayMongoKeyLooksLikeSecretKey(string? key)
{
    if (string.IsNullOrWhiteSpace(key)) return false;
    var trimmed = key.Trim();
    return trimmed.StartsWith("sk_test_", StringComparison.Ordinal)
        || trimmed.StartsWith("sk_live_", StringComparison.Ordinal);
}

// --- REDIS CACHING ---
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnection))
{
    try
    {
        Log.Information("Configuring Redis cache: {RedisConnection}", redisConnection);
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = RedisCacheService.InstanceName;
        });
        // IConnectionMultiplexer (registered by the Map module) enables working
        // prefix invalidation; resolved lazily so registration order doesn't matter.
        builder.Services.AddSingleton<ICacheService>(sp => new RedisCacheService(
            sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
            sp.GetService<StackExchange.Redis.IConnectionMultiplexer>()));
        Log.Information("Redis cache configured successfully");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to configure Redis cache, falling back to in-memory cache. Error: {Message}", ex.Message);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ICacheService, MemoryCacheService>();
    }
}
else
{
    Log.Information("Redis connection not configured, using in-memory cache");
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<ICacheService, MemoryCacheService>();
}

// --- JWT AUTHENTICATION ---
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var jwtSecret = jwtSettings["Secret"];
var jwtIssuer = jwtSettings["Issuer"];
var jwtAudience = jwtSettings["Audience"];

if (string.IsNullOrEmpty(jwtSecret))
{
    Log.Fatal("JWT Secret is required but not configured. Please set JwtSettings__Secret environment variable.");
    throw new InvalidOperationException("JWT Secret is required. Set JwtSettings__Secret environment variable.");
}

if (string.IsNullOrEmpty(jwtIssuer))
{
    Log.Warning("JWT Issuer not configured, using default: BeeLogisticsApi");
    jwtIssuer = "BeeLogisticsApi";
}

if (string.IsNullOrEmpty(jwtAudience))
{
    Log.Warning("JWT Audience not configured, using default: BeeLogisticsClient");
    jwtAudience = "BeeLogisticsClient";
}

// Clerk settings (additive): when empty, only the legacy HS256 scheme is wired,
// so existing apps + the backoffice keep working with no behaviour change.
var clerkSettings = new ClerkSettings();
builder.Configuration.GetSection(ClerkSettings.SectionName).Bind(clerkSettings);
builder.Services.Configure<ClerkSettings>(builder.Configuration.GetSection(ClerkSettings.SectionName));
var clerkEnabled = clerkSettings.IsEnabled;
Log.Information("Clerk authentication {State}", clerkEnabled ? "ENABLED" : "disabled (no Clerk config)");

const string SmartJwtScheme = "SmartJwt";
const string ClerkScheme = "Clerk";

// SignalR token extraction (Authorization header, fallback to query string for WebSockets).
// Shared by both JWT schemes.
static JwtBearerEvents BuildHubTokenEvents() => new()
{
    OnMessageReceived = context =>
    {
        var path = context.HttpContext.Request.Path;
        if (path.StartsWithSegments("/hubs"))
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                context.Token = authHeader.Substring("Bearer ".Length).Trim();
            }
            else
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Token = accessToken;
                }
            }
        }
        return Task.CompletedTask;
    }
};

var authBuilder = builder.Services.AddAuthentication(options =>
{
    // The default scheme is a policy scheme that routes each request:
    // X-Api-Key header → ServiceApiKey (trusted S2S clients like the back-office
    // backend), Clerk-issued tokens → Clerk (when enabled), otherwise the legacy
    // HS256 validator.
    options.DefaultAuthenticateScheme = SmartJwtScheme;
    options.DefaultChallengeScheme = SmartJwtScheme;
});

// Legacy HS256 scheme (unchanged) — keeps current tokens valid during transition.
authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
    };
    options.Events = BuildHubTokenEvents();
});

var clerkIssuer = string.IsNullOrWhiteSpace(clerkSettings.Issuer) ? clerkSettings.Authority : clerkSettings.Issuer;

if (clerkEnabled)
{
    // Clerk scheme: RS256 validated via JWKS discovered from the Clerk Authority.
    authBuilder.AddJwtBearer(ClerkScheme, options =>
    {
        if (!string.IsNullOrWhiteSpace(clerkSettings.Authority))
        {
            options.Authority = clerkSettings.Authority;
        }
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(clerkIssuer),
            ValidIssuer = clerkIssuer,
            ValidateAudience = !string.IsNullOrWhiteSpace(clerkSettings.Audience),
            ValidAudience = clerkSettings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // Map Clerk's `sub` onto NameIdentifier so existing code (LivenessController,
            // claims transformation) resolves the user id consistently.
            NameClaimType = ClaimTypes.NameIdentifier
        };
        options.Events = BuildHubTokenEvents();
    });
}

// ServiceApiKey scheme: machine-to-machine auth for trusted service clients
// (e.g. the back-office backend). Validates X-Client-Id + X-Api-Key against
// SHA-256 hashes in ServiceClients config (Vault-overlaid).
builder.Services.Configure<ServiceClientsOptions>(builder.Configuration.GetSection(ServiceClientsOptions.SectionName));
authBuilder.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ServiceApiKeyAuthenticationHandler>(
    ServiceApiKeyDefaults.SchemeName, null);

// Policy scheme: route by request shape — API key header first, then token issuer.
authBuilder.AddPolicyScheme(SmartJwtScheme, SmartJwtScheme, options =>
{
    options.ForwardDefaultSelector = context =>
    {
        if (context.Request.Headers.ContainsKey(ServiceApiKeyDefaults.ApiKeyHeader))
        {
            return ServiceApiKeyDefaults.SchemeName;
        }

        string? token = null;
        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader.Substring("Bearer ".Length).Trim();
        }
        else if (context.Request.Path.StartsWithSegments("/hubs"))
        {
            token = context.Request.Query["access_token"];
        }

        if (clerkEnabled && !string.IsNullOrEmpty(token) && !string.IsNullOrWhiteSpace(clerkIssuer))
        {
            var handler = new JwtSecurityTokenHandler();
            if (handler.CanReadToken(token))
            {
                try
                {
                    var issuer = handler.ReadJwtToken(token).Issuer;
                    if (string.Equals(issuer?.TrimEnd('/'), clerkIssuer.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        return ClerkScheme;
                    }
                }
                catch
                {
                    // Unreadable token → fall through to legacy scheme, which will reject it.
                }
            }
        }
        return JwtBearerDefaults.AuthenticationScheme;
    };
});

builder.Services.AddAuthorization(options =>
{
    // Backoffice policy: user must have is_backoffice claim AND SuperAdmin/Admin role (set only by backoffice-login)
    options.AddPolicy("Backoffice", policy => policy.RequireAssertion(context =>
    {
        var isBackoffice = context.User.FindFirst("is_backoffice")?.Value == "true";
        var role = context.User.FindFirst("role")?.Value;
        var roleClaims = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
        var allowedRoles = new[] { "SuperAdmin", "Admin" };
        var hasAdminRole = (role != null && allowedRoles.Contains(role)) || roleClaims.Any(r => allowedRoles.Contains(r));
        return isBackoffice && hasAdminRole;
    }));
    // Policy for driver-applications admin endpoints: same as Backoffice (alias for backward compatibility)
    options.AddPolicy("BackofficeDriverApplications", policy => policy.RequireAssertion(context =>
    {
        var isBackoffice = context.User.FindFirst("is_backoffice")?.Value == "true";
        var role = context.User.FindFirst("role")?.Value;
        var roleClaims = context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
        var allowedRoles = new[] { "SuperAdmin", "Admin" };
        var hasAdminRole = (role != null && allowedRoles.Contains(role)) || roleClaims.Any(r => allowedRoles.Contains(r));
        return isBackoffice && hasAdminRole;
    }));
});

// --- CURRENT CONTEXT (single-business instance) ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICorrelationIdAccessor, CorrelationIdAccessor>();
builder.Services.AddScoped(typeof(CorrelationIdSendFilter<>));

// --- CORS ---
// SECURITY: Restrict CORS to specific origins, headers, and methods
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
Log.Information("CORS Allowed Origins: {Origins}", string.Join(", ", allowedOrigins));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              // SECURITY: Specify exact headers instead of AllowAnyHeader to prevent header injection
              .WithHeaders(
                  "Content-Type",
                  "Authorization",
                  "X-Requested-With",
                  "Accept",
                  "Accept-Language",
                  "X-CSRF-Token", // For CSRF protection if implemented
                  // SignalR required headers
                  "x-signalr-user-agent" // Required for SignalR negotiate requests
              )
              // SECURITY: Specify exact methods instead of AllowAnyMethod
              .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
              .AllowCredentials();
    });
});

// --- SWAGGER/OPENAPI DOCUMENTATION ---
// Off by default everywhere (deployed dev runs as Development and is internet-facing).
// Opt in explicitly on a local machine via Swagger__Enabled=true.
var swaggerEnabled = builder.Configuration.GetValue<bool>("Swagger:Enabled", false);
if (swaggerEnabled)
    builder.Services.AddSwaggerDocumentation();

// --- SIGNALR ---
// Redis backplane so hub broadcasts (driver locations, booking events) reach
// clients on every replica, not just the node they're connected to.
var signalRBuilder = builder.Services.AddSignalR();
if (!string.IsNullOrEmpty(redisConnection))
{
    // Scopes the backplane to this deployment (#79). Redis pub/sub is NOT database-scoped, so the
    // ,defaultDatabase= that isolates cache keys does nothing here — without a distinct prefix a
    // BookingStatusChanged broadcast in one deployment reaches that user's connections in another.
    // Falls back to the historical literal so a missing variable degrades to today's behaviour
    // rather than to silence.
    var signalRChannelPrefix = builder.Configuration["SignalR:ChannelPrefix"] ?? "BeeLogistics:signalr:";

    signalRBuilder.AddStackExchangeRedis(redisConnection, options =>
    {
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(signalRChannelPrefix);
    });
    Log.Information("SignalR Redis backplane configured with channel prefix {ChannelPrefix}", signalRChannelPrefix);
}
else
{
    Log.Warning("SignalR running without a Redis backplane; broadcasts only reach clients on this instance");
}
builder.Services.AddScoped<INotificationService, SignalRNotificationService>();
builder.Services.AddScoped<ICustomerIdentityResolver, CustomerIdentityResolver>();
builder.Services.AddScoped<IDriverOutboxPublisher, DriverOutboxPublisher>();
builder.Services.AddScoped<DriversOutboxDispatcherService>();
builder.Services.AddScoped<BeeLogistics.Modules.Payment.Application.Interfaces.IPaymentOutboxPublisher,
    BeeLogistics.Modules.Payment.Application.Interfaces.PaymentOutboxPublisher>();
builder.Services.AddScoped<PaymentOutboxDispatcherService>();

// --- VIRUS SCANNING (ClamAV) ---
builder.Services.AddSingleton<BeeLogistics.Shared.Abstractions.IVirusScanner, BeeLogistics.Shared.Infrastructure.ClamAVScanner>();

// --- IMAGE PROCESSING ---
builder.Services.AddSingleton<BeeLogistics.Shared.Abstractions.IImageProcessor, BeeLogistics.Shared.Infrastructure.ImageProcessor>();

// --- FILE STORAGE (Strategy pattern) ---
// Every configured provider registers as IFileStorageProvider simultaneously (Local, NoOp, and one
// S3CompatibleFileStorageProvider instance per S3-compatible profile below). FileStorageRouter is the
// only IFileStorageService consumers ever inject; it asks IFileStorageProviderFactory for the provider
// named by the live "FileStorage:Provider" value on every call ("Local" | "S3" | "DigitalOcean"), so
// switching providers takes effect on the next file operation without an app restart.
builder.Services.AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetSection(FileStorageOptions.SectionName));

builder.Services.AddSingleton<IFileStorageProvider, LocalFileStorageService>();
builder.Services.AddSingleton<IFileStorageProvider, NoOpFileStorageService>();

RegisterS3CompatibleProvider("S3");
RegisterS3CompatibleProvider("DigitalOcean");

builder.Services.AddSingleton<IFileStorageProviderFactory, FileStorageProviderFactory>();
builder.Services.AddSingleton<IFileStorageService, FileStorageRouter>();

void RegisterS3CompatibleProvider(string providerName)
{
    var section = builder.Configuration.GetSection(providerName);
    var accessKey = section["AccessKey"];
    var secretKey = section["SecretKey"];
    if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
    {
        Log.Information("File storage provider '{Provider}' has no AccessKey/SecretKey configured - skipping", providerName);
        return;
    }

    // Don't touch the network during EF design-time tooling (migrations, etc.)
    if (Environment.GetCommandLineArgs().Any(arg => arg.Contains("ef", StringComparison.OrdinalIgnoreCase)))
        return;

    // Endpoint is required once credentials are present, and is resolved outside the try below so a
    // bad *configuration* stays loud instead of being downgraded to a warning. The old
    // `section["Endpoint"] ?? "localhost:8333"` only guarded against null, so a declared-but-empty
    // setting (empty CI/CD variable, blank secret) reached the storage client as "" and threw -
    // leaving the provider unregistered and every upload silently routed to NoOp. (#42)
    if (!ObjectStorageEndpoint.TryNormalize(section["Endpoint"], out var cleanEndpoint))
    {
        ReportProviderMisconfiguration(
            providerName,
            $"'{providerName}:Endpoint' is missing or empty - it must be a host such as 'sgp1.digitaloceanspaces.com'");
        return;
    }

    try
    {
        // Region has the same null-only hole: an empty value must fall back to "auto" rather than
        // being signed as "", but note providers like DigitalOcean Spaces need their real region.
        var region = section["Region"];
        var options = new ObjectStorageProviderOptions
        {
            Endpoint = cleanEndpoint,
            CdnEndpoint = section["CdnEndpoint"],
            Region = string.IsNullOrWhiteSpace(region) ? "auto" : region.Trim(),
            AccessKey = accessKey,
            SecretKey = secretKey,
            UseSSL = section.GetValue<bool>("UseSSL", false),
            BucketName = section["BucketName"],
            ObjectKeyPrefix = section["ObjectKeyPrefix"],
        };

        Log.Information(
            "Initializing S3-compatible client '{Provider}' with endpoint: {Endpoint} (region: {Region}, UseSSL: {UseSSL})",
            providerName, cleanEndpoint, options.Region, options.UseSSL);

        // Using MinIO .NET client library (S3-compatible) - works with R2, AWS S3, DigitalOcean
        // Spaces, MinIO server, etc. Region must match the provider's expected value for request
        // signing (e.g. DigitalOcean Spaces requires the actual region like "sgp1", not "auto").
        var minioClient = new MinioClient()
            .WithEndpoint(cleanEndpoint)
            .WithCredentials(options.AccessKey, options.SecretKey)
            .WithRegion(options.Region)
            .WithSSL(options.UseSSL)
            .Build();

        builder.Services.AddSingleton<IFileStorageProvider>(sp => new S3CompatibleFileStorageProvider(
            minioClient,
            options,
            providerName,
            sp.GetRequiredService<ILogger<S3CompatibleFileStorageProvider>>()));

        Log.Information("Registered S3-compatible file storage provider '{Provider}'", providerName);
    }
    catch (Exception ex)
    {
        // Log but don't fail startup if this provider is unavailable (e.g. during migrations) -
        // FileStorageProviderFactory falls back to NoOp if this profile ends up selected.
        // Configuration problems are caught before this block so they can be fatal instead.
        Log.Warning(ex, "Could not initialize S3-compatible client for provider '{Provider}': {Message}", providerName, ex.Message);
    }
}

// Credentials were configured for this profile, so a broken setting is an operator mistake rather
// than an absent provider - always report it at Error. FileStorage:Provider is read through
// IOptionsMonitor and can be switched at runtime, so an inactive profile is worth shouting about
// too; only the profile that is active at startup is fatal, and only outside Development.
void ReportProviderMisconfiguration(string providerName, string reason)
{
    Log.Error(
        "File storage provider '{Provider}' has credentials configured but cannot be initialized: {Reason}",
        providerName, reason);

    var activeProvider = builder.Configuration[$"{FileStorageOptions.SectionName}:Provider"];
    if (!string.Equals(activeProvider, providerName, StringComparison.OrdinalIgnoreCase))
        return;

    if (builder.Environment.IsDevelopment())
    {
        Log.Error(
            "'{Provider}' is the active FileStorage:Provider - uploads will fall back to NoOp and silently store nothing",
            providerName);
        return;
    }

    throw new InvalidOperationException(
        $"File storage provider '{providerName}' is selected by {FileStorageOptions.SectionName}:Provider but {reason}. " +
        "Refusing to start with uploads silently routed to NoOp.");
}

// --- MODULES (with Controllers) ---
// SECURITY: Configure global request size limits to prevent DoS attacks
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 20_000_000; // 20MB max for multipart (file uploads)
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.Services.AddControllers(options =>
{
    // SECURITY: Global request size limit (can be overridden per endpoint with [RequestSizeLimit])
    options.MaxModelBindingCollectionSize = 1000; // Limit collection size
})
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    })
    .AddIdentityModule(connectionString)
    .AddBookingsModule(connectionString)
    .AddRatingModule(connectionString)
    .AddPaymentModule(connectionString, builder.Configuration)
    .AddChatModule(connectionString)
    // Messaging owns the appservice endpoint Synapse calls; MVC has to find its controllers.
    .AddApplicationPart(typeof(BeeLogistics.Modules.Messaging.DependencyInjection).Assembly)
    .AddCrmModule(connectionString, builder.Configuration)
    .AddDriversModule(connectionString)
    .AddRevenueModule(connectionString)
    .AddReferralsModule(connectionString)
    .AddGiveawaysModule(connectionString)
    .AddOffersModule(connectionString)
    .AddNotificationModule(connectionString, builder.Configuration)
    .AddFraudModule(connectionString)
    .AddAccountingModule(connectionString)
    .AddVerificationModule(builder.Configuration, connectionString);

// Validated at startup: the commission rate is a fraction (0.05 = 5%), it is settable from Vault,
// and a value outside 0..1 otherwise boots fine and then throws inside EarningsSplit — surfacing as
// every driver's offer list 500ing rather than as a bad deploy.
builder.Services.AddSingleton<IValidateOptions<DriverWalletOptions>, DriverWalletOptionsValidator>();
builder.Services.AddOptions<DriverWalletOptions>()
    .Bind(builder.Configuration.GetSection(DriverWalletOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<BeeLogistics.Modules.Payment.Application.PaymentRefundOptions>,
    BeeLogistics.Modules.Payment.Application.PaymentRefundOptionsValidator>();
builder.Services.AddOptions<BeeLogistics.Modules.Payment.Application.PaymentRefundOptions>()
    .Bind(builder.Configuration.GetSection(BeeLogistics.Modules.Payment.Application.PaymentRefundOptions.SectionName))
    .ValidateOnStart();

// Reconciliation: validated at startup because a too-short grace window would expire checkouts
// customers are still paying, and that is not something to discover at runtime.
builder.Services.AddSingleton<IValidateOptions<BeeLogistics.Modules.Payment.Application.PaymentReconciliationOptions>,
    BeeLogistics.Modules.Payment.Application.PaymentReconciliationOptionsValidator>();
builder.Services.AddOptions<BeeLogistics.Modules.Payment.Application.PaymentReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(BeeLogistics.Modules.Payment.Application.PaymentReconciliationOptions.SectionName))
    .ValidateOnStart();

// Location trail retention: validated at startup because a too-short window would delete trail
// data for bookings still in dispute (the validator and binding live in the Map module).
builder.Services.AddOptions<BeeLogistics.Modules.Map.Application.LocationRetentionOptions>()
    .Bind(builder.Configuration.GetSection(BeeLogistics.Modules.Map.Application.LocationRetentionOptions.SectionName))
    .ValidateOnStart();

// Payment webhook: sync driver top-up crediting so wallet updates without relying on message bus
builder.Services.AddScoped<IPaymentWebhookPostProcessor, DriverTopUpWebhookPostProcessor>();
// Disbursement webhook: withdrawal completion/failure (same auth as checkout webhook)
builder.Services.AddScoped<IDisbursementWebhookProcessor, DisbursementWebhookProcessor>();
builder.Services.AddScoped<IPayMongoAccountWebhookProcessor, PayMongoAccountWebhookProcessor>();
builder.Services.AddScoped<IPayMongoQrWebhookProcessor, PayMongoQrWebhookProcessor>();

// Real-time top-up paid events (SSE + SignalR) so driver app can update wallet/history immediately when webhook is received
builder.Services.AddSingleton<IDriverTopUpEventBroadcaster, DriverTopUpEventBroadcaster>();

// Real-time booking payment paid events (SSE + SignalR) so customer app can update payment status immediately when webhook is received
builder.Services.AddSingleton<IBookingPaymentEventBroadcaster, BookingPaymentEventBroadcaster>();

builder.Services.Configure<BeeLogistics.Modules.Fraud.Application.FraudOptions>(
    builder.Configuration.GetSection(BeeLogistics.Modules.Fraud.Application.FraudOptions.SectionName));

// Map Module (separate registration as it doesn't return IMvcBuilder)
builder.Services.AddMapModule(builder.Configuration);

// Messaging Module — Matrix appservice for per-booking driver↔customer chat.
// Registers options and an HttpClient only; everything it does is gated on Matrix:Enabled.
builder.Services.AddMessagingModule(builder.Configuration, connectionString);

// --- HANGFIRE (Background Jobs) ---
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options =>
    {
        options.UseNpgsqlConnection(connectionString);
    }, new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire",
        QueuePollInterval = TimeSpan.FromSeconds(15),
        JobExpirationCheckInterval = TimeSpan.FromHours(1),
        CountersAggregateInterval = TimeSpan.FromMinutes(5),
        PrepareSchemaIfNecessary = true
    }));

// Review apps for an MR without a migration share bee_logistics_db_dev, and therefore share the
// hangfire schema inside it. A second Hangfire server on that storage is not a harmless duplicate:
// it dispatches bookings, drains the payment and drivers outboxes and deletes InboxState rows
// against dev's real data, and it re-registers recurring jobs from its own older assembly — which
// permanently disables any job whose type that assembly does not contain. review-108 did exactly
// that for three days, and the symptom surfaced as "the reconciler will not schedule".
//
// The CI already isolates the RabbitMQ vhost, the SignalR prefix and the MQTT topic per review app.
// Hangfire lives inside the database, so that isolation cannot reach it; this flag is how it does.
// Defaults to true, leaving dev, staging and production unchanged.
var runHangfireServer = builder.Configuration.GetValue("Hangfire:EnableServer", true);

if (runHangfireServer)
{
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = 2; // Increased for email processing
        options.ServerTimeout = TimeSpan.FromMinutes(4);
        options.SchedulePollingInterval = TimeSpan.FromSeconds(15);
        options.Queues = new[] { "default", "emails" }; // Dedicated queue for emails
    });
}
else
{
    Log.Warning(
        "Hangfire server DISABLED (Hangfire:EnableServer=false). This instance shares another "
        + "environment's database, so running background jobs here would duplicate work on data it "
        + "does not own. The API itself is unaffected.");
}

// Register queue service for Hangfire
builder.Services.AddScoped<BookingBroadcastQueueService>();
builder.Services.AddScoped<DriverTopUpReconciliationService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.WalletUpkeepFeeService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.DriverPackageInsuranceLapseService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.PayMongoWalletSweepService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.PayMongoWithdrawalService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.ChildWalletWithdrawalReconciliationService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.PayMongoBalanceReconciliationService>();
builder.Services.AddScoped<BeeLogistics.Modules.Drivers.Application.Services.WalletTransferReconciliationService>();
builder.Services.AddScoped<WithdrawalReconciliationService>();
builder.Services.AddScoped<PaymentReconciliationService>();
builder.Services.AddScoped<BeeLogistics.Api.Services.LocationHistoryRetentionJob>();

// --- MASSTRANSIT (RabbitMQ) ---
var rabbitMqHost = builder.Configuration["RabbitMq:Host"] ?? "localhost";
var rabbitMqPort = builder.Configuration["RabbitMq:Port"] ?? "5672";
var rabbitMqUser = builder.Configuration["RabbitMq:Username"] ?? "guest";
var rabbitMqPass = builder.Configuration["RabbitMq:Password"] ?? "guest";
var rabbitMqVHost = builder.Configuration["RabbitMq:VirtualHost"] ?? "/";

// Back-office webhook dispatcher (bee-backend → backoffice-backend, HMAC-signed)
builder.Services.AddIntegrationModule(builder.Configuration);

builder.Services.AddMassTransit(x =>
{
    // Register Consumers from Modules
    x.AddConsumer<BeeLogistics.Modules.Integration.Application.Consumers.BackofficeWebhookConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Bookings.Application.Consumers.BookingBroadcastConsumer>();
    x.AddConsumer<CashDeliverySettlementConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Drivers.Application.Consumers.EarningCreditConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Drivers.Application.Consumers.UserDeletedIntegrationEventConsumer>();
    x.AddConsumer<DriverTopUpWebhookConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Drivers.Application.Consumers.PaymentRefundedEventConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Payment.Application.Consumers.BookingPaymentWebhookConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Payment.Application.Consumers.BookingCancelledPaymentConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Notification.Application.Consumers.WithdrawalReceiptEmailConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Notification.Application.Consumers.SendPushConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Notification.Application.Consumers.SendNotificationConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Bookings.Application.Consumers.BookingPaymentReceiptEmailConsumer>();
    // Matrix booking chat (#78). Room provisioning runs off the booking-created event.
    x.AddConsumer<BeeLogistics.Modules.Messaging.Application.Consumers.BookingChatRoomRequestedConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Messaging.Application.Consumers.BookingChatDriverAssignedConsumer>();
    x.AddConsumer<BeeLogistics.Modules.Messaging.Application.Consumers.BookingChatStatusChangedConsumer>();
    // Register Location Module Consumers
    x.AddLocationConsumers(); // This registers DriverLocationUpdatedConsumer
    // Register Fraud Module Consumers
    x.AddFraudConsumers();
    // Register Accounting Module Consumers
    x.AddAccountingConsumers();
    // Register Revenue Module Consumers
    x.AddRevenueConsumers();

    // Configure Entity Framework Core Outbox for transactional message publishing
    // This ensures messages are only published after database transaction commits
    x.AddEntityFrameworkOutbox<BeeLogistics.Modules.Bookings.Infrastructure.BookingsDbContext>(o =>
    {
        // Query delay: how often to check for pending outbox messages
        o.QueryDelay = TimeSpan.FromSeconds(10);
        
        // Use PostgreSQL for outbox storage (same database as BookingsDbContext)
        o.UsePostgres();
        
        // Bus outbox: enables transactional publishing within request scope
        o.UseBusOutbox();
    });

    // Payment used to have a second MassTransit EF Core bus outbox here (GitLab #65, to stop
    // PaymentRefundedEvent being lost on a RabbitMQ outage). MassTransit 8.x supports only one
    // DbContext per bus for UseBusOutbox() - registering it on both BookingsDbContext and
    // PaymentDbContext gave each its own BusOutboxDeliveryService background loop, and both
    // loops share one singleton IBusOutboxNotification. Racing on that shared object's internal
    // CancellationTokenSource crashed the host with a NullReferenceException in
    // BusOutboxNotification.WaitForDelivery. Multi-DbContext bus outbox support only landed in
    // MassTransit 9.2.0.
    //
    // Payment now has its own hand-rolled outbox instead (mirroring the Drivers module's - see
    // IPaymentOutboxPublisher, PaymentOutboxDispatcherService below), independent of MassTransit's
    // bus-outbox machinery entirely, so there is no second delivery loop to race with this one.

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, ushort.Parse(rabbitMqPort), rabbitMqVHost, h =>
        {
            h.Username(rabbitMqUser);
            h.Password(rabbitMqPass);
        });

        // Propagate X-Correlation-Id on all outgoing messages (from HTTP or from consumer chain)
        cfg.UseSendFilter(typeof(CorrelationIdSendFilter<>), context);

        // Consumer fault handling: without this, a single exception sends the message
        // straight to the _error queue. Immediate retries cover transient faults
        // (deadlocks, Redis blips); delayed redelivery covers short outages.
        // Consumers must stay idempotent (they are: status-guarded updates).
        cfg.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)));
        cfg.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30)));

        // Configure endpoints for registered consumers
        cfg.ConfigureEndpoints(context);
    });
});

// Notification push/dispatch — DirectBusPublisher uses IBus (same as payment webhooks).
builder.Services.AddScoped<IDirectBusPublisher, BeeLogistics.Api.Services.DirectBusPublisher>();

// --- OPENTELEMETRY ---
// Metrics are always on, scraped from /metrics (Prometheus format):
// ASP.NET Core + HttpClient + runtime, MassTransit bus, Npgsql, and the
// BeeLogistics domain meter (offers, matching, webhooks, refunds, outbox).
// Distributed tracing turns on when OpenTelemetry:OtlpEndpoint is configured.
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "bee-backend",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"));

otel.WithMetrics(metrics => metrics
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddRuntimeInstrumentation()
    .AddMeter(BeeLogistics.Shared.Infrastructure.BeeMetrics.MeterName)
    .AddMeter(MassTransit.Monitoring.InstrumentationOptions.MeterName)
    .AddMeter("Npgsql")
    .AddPrometheusExporter());

var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    otel.WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddSource(MassTransit.Logging.DiagnosticHeaders.DefaultListenerName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
    Log.Information("OpenTelemetry tracing enabled, exporting to {OtlpEndpoint}", otlpEndpoint);
}

// --- HEALTH CHECKS ---
// /health/live: process is responsive. /health/ready: dependencies are reachable.
// MassTransit registers its own bus readiness checks (tagged "ready") via AddMassTransit.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IdentityAppDbContext>("postgres", tags: new[] { "ready" })
    .AddCheck<BeeLogistics.Api.Services.RedisHealthCheck>("redis", tags: new[] { "ready" })
    // Reads observed traffic outcomes; never calls the provider, so it adds no latency to the
    // probe and cannot itself become an outage. Covers the active gateway only.
    .AddCheck<BeeLogistics.Modules.Payment.Infrastructure.Services.PaymentGatewayHealthCheck>("payment-gateway", tags: new[] { "ready" })
    // Tagged "ready" for visibility but reports Degraded rather than Unhealthy, so a Synapse
    // outage shows up in the payload without pulling the API out of rotation — booking chat is
    // not on the critical path for creating or completing a delivery. See MatrixHealthCheck.
    .AddCheck<BeeLogistics.Modules.Messaging.Infrastructure.MatrixHealthCheck>("matrix", tags: new[] { "ready" });

// --- MEDIATR ---
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblies(
        typeof(Program).Assembly,
        typeof(IdentityAppDbContext).Assembly,
        typeof(BookingsDbContext).Assembly,
        typeof(PaymentDbContext).Assembly,
        typeof(ChatDbContext).Assembly,
        typeof(CrmDbContext).Assembly,
        typeof(DriversDbContext).Assembly,
        typeof(BeeLogistics.Modules.Referrals.Infrastructure.ReferralsDbContext).Assembly,
        typeof(BeeLogistics.Modules.Giveaways.Infrastructure.GiveawaysDbContext).Assembly,
        typeof(BeeLogistics.Modules.Rating.Infrastructure.RatingDbContext).Assembly,
        typeof(RevenueDbContext).Assembly
    );
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
});

// --- PII/SII ENCRYPTION KEYS (HashiCorp Vault) ---
// Load AES master key + HMAC blind-index key from Vault ONCE at startup. Fail fast if Vault is
// unreachable or the secret is missing — the app must never start silently unable to read PII.
// Reuses the same vaultOptions read during the config-pipeline bootstrap above.
using (var vaultLoggerFactory = LoggerFactory.Create(lb => lb.AddSerilog()))
{
    var vaultKeyProvider = new VaultKeyProvider(vaultOptions, vaultLoggerFactory.CreateLogger<VaultKeyProvider>());
    try
    {
        await vaultKeyProvider.LoadAsync();
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Failed to load PII encryption keys from Vault at {Address} ({Mount}/{Path}). The application cannot start.", vaultOptions.Address, vaultOptions.MountPath, vaultOptions.SecretPath);
        throw;
    }
    builder.Services.AddSingleton<IEncryptionKeyProvider>(vaultKeyProvider);
}
builder.Services.AddSingleton<IDataProtectorService, BeeLogistics.Shared.Infrastructure.Security.DataProtectorService>();

// Blind-index interceptor: keeps the searchable hash columns in sync with their encrypted source
// fields. Mappings are declared here (decoupled from the modules) by entity type name.
builder.Services.AddSingleton(sp => new BlindIndexSaveChangesInterceptor(
    sp.GetRequiredService<IDataProtectorService>(),
    new[]
    {
        new BlindIndexSaveChangesInterceptor.Mapping("ApplicationUser", "PhoneNumber", "PhoneNumberHash", "phone"),
        new BlindIndexSaveChangesInterceptor.Mapping("DriverApplication", "Email", "EmailHash", "email"),
    }));

var app = builder.Build();

// Sentry's SDK init is normally deferred to SentryStartupFilter, which only runs once the request
// pipeline is built — i.e. at app.RunAsync() at the bottom of this file. (Sentry's own logger
// provider would otherwise force it earlier, but UseSerilog() replaced the MEL backend so that
// provider is never constructed.) Resolving the singleton Func<IHub> here runs the init exactly
// once, up front, so the migrations, seeding and Hangfire registrations below are covered too.
// The later app.UseSentry() from the startup filter reuses this same singleton.
if (!string.IsNullOrWhiteSpace(sentryDsn))
{
    _ = app.Services.GetRequiredService<Func<IHub>>();
    Log.Information("Sentry error monitoring enabled (environment={Environment}, release={Release})",
        app.Environment.EnvironmentName, Environment.GetEnvironmentVariable("SENTRY_RELEASE") ?? "(none)");
}

Log.Information("Application built successfully, configuring middleware...");

// --- CORS --- (MUST be before rate limiting so CORS headers are added to ALL responses including 429)
// CORS must be applied early to handle preflight OPTIONS requests
app.UseCors();

// --- CORRELATION ID --- (early so every request gets a trace ID; used in logs and MassTransit)
app.UseMiddleware<CorrelationIdMiddleware>();

// --- SWAGGER/OPENAPI DOCUMENTATION ---
if (swaggerEnabled)
{
    app.UseSwaggerDocumentation();
    Log.Information("Swagger documentation enabled");
}

// --- SECURITY MIDDLEWARE (OWASP) ---
// OWASP: Remove server identification headers early in pipeline (before other middleware)
// BUT: Don't remove CORS headers! Skip for API endpoints and Swagger
app.Use(async (context, next) =>
{
    // Remove server identification headers immediately
    // Skip for API endpoints, Swagger, and SignalR hubs to avoid interfering with CORS headers
    if (!context.Request.Path.StartsWithSegments("/api") && 
        !context.Request.Path.StartsWithSegments("/hubs") &&
        !context.Request.Path.StartsWithSegments("/swagger"))
    {
        context.Response.Headers.Remove("Server");
        context.Response.Headers.Remove("X-Powered-By");
        context.Response.Headers.Remove("X-AspNet-Version");
        context.Response.Headers.Remove("X-AspNetMvc-Version");
    }
    await next();
});

app.UseMiddleware<SecurityHeadersMiddleware>();

// --- METRICS ENDPOINT PROTECTION ---
// /metrics (Prometheus) leaks route/dependency/business telemetry and is not otherwise
// authenticated. Require a bearer scrape token; fail closed (404) when none is configured so
// it is never exposed anonymously. Prometheus scrape config: authorization.credentials=<token>.
var metricsScrapeToken = app.Configuration["Metrics:ScrapeToken"];
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/metrics"))
    {
        if (string.IsNullOrWhiteSpace(metricsScrapeToken))
        {
            context.Response.StatusCode = 404;
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var provided = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : string.Empty;

        var ok = provided.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(metricsScrapeToken));
        if (!ok)
        {
            context.Response.StatusCode = 401;
            return;
        }
    }

    await next();
});

// SECURITY: Rate limiting enabled to prevent brute force attacks and DDoS
// Skip rate limiting for Swagger endpoints
app.UseMiddleware<RateLimitingMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseStatusCodePages(async statusContext =>
{
    var httpContext = statusContext.HttpContext;
    var requestPath = httpContext.Request.Path;
    var response = httpContext.Response;

    // Only standardize unmatched API routes; keep non-API behavior unchanged.
    if (!requestPath.StartsWithSegments("/api") || response.StatusCode != StatusCodes.Status404NotFound)
    {
        return;
    }

    // Ensure consistent response body for framework-generated 404s.
    response.ContentType = "application/json";
    if (!response.Headers.ContainsKey("X-Content-Type-Options"))
    {
        response.Headers.Append("X-Content-Type-Options", "nosniff");
    }

    var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.CorrelationIdItemKey, out var cid) && cid is string id
        ? id
        : Guid.NewGuid().ToString("N");
    response.Headers.Append("X-Correlation-Id", correlationId);

    var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Api404");
    logger.LogWarning("Unmatched API route: {Method} {Path} from {IP}. CorrelationId: {CorrelationId}",
        httpContext.Request.Method,
        requestPath,
        httpContext.Connection.RemoteIpAddress,
        correlationId);

    await response.WriteAsJsonAsync(new
    {
        success = false,
        message = "API endpoint not found",
        correlationId
    });
});

Log.Information("Security middleware configured");

// --- SEED DATABASE ---
var isSeedOnlyMode = Environment.GetCommandLineArgs().Any(arg => 
    arg.Equals("--seed-only", StringComparison.OrdinalIgnoreCase));

try
{
    Log.Information("Starting database seeding...");
    using (var scope = app.Services.CreateScope())
    {
        await DbSeeder.SeedAsync(scope.ServiceProvider);
    }
    Log.Information("Database seeding completed successfully");
    
    // If --seed-only flag is set, exit after seeding
    if (isSeedOnlyMode)
    {
        Log.Information("Seed-only mode: Exiting after seeding");
        return;
    }
}
catch (Exception ex)
{
    Log.Error(ex, "Database seeding failed, but continuing startup. Error: {Message}", ex.Message);
    // If seed-only mode, exit on error; otherwise continue
    if (isSeedOnlyMode)
    {
        Log.Fatal(ex, "Seed-only mode: Exiting due to seeding failure");
        return;
    }
    // Don't crash the app if seeding fails - it might already be seeded
}

// Configure the HTTP request pipeline
app.UseHttpsRedirection();

// Serve static files (including uploads)
app.UseStaticFiles();

// Serve uploads directory at /uploads path
var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
if (Directory.Exists(uploadsPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
        RequestPath = "/uploads"
    });
}
// CORS is now applied earlier (before rate limiting middleware)
app.UseAuthentication();
// Token blacklist check (after authentication, before authorization)
app.UseMiddleware<TokenBlacklistMiddleware>();
app.UseAuthorization();
// Backoffice authorization middleware (must be after authentication/authorization)
app.UseMiddleware<BackofficeAuthorizationMiddleware>();

// Hangfire dashboard intentionally not mounted: the deployed dev environment runs as
// Development on the public internet, and the dashboard has no real authorization.
// Job monitoring moves to the Aspire dashboard / logs instead.

// Enable request body buffering for Xendit webhook so we can verify RSA signature against raw body
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase),
    branch => branch.Use(async (ctx, next) =>
    {
        ctx.Request.EnableBuffering();
        await next();
    }));

app.MapControllers();

// Liveness: no dependency checks — only "is the process serving requests".
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
// Readiness: Postgres, Redis, and the MassTransit bus must be reachable.
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// Prometheus scrape endpoint (/metrics). Access is gated by the bearer-token middleware
// above (Metrics:ScrapeToken); fail-closed when unconfigured.
app.MapPrometheusScrapingEndpoint();

app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<LocationHub>("/hubs/location");

// Map Scalar API Reference (only when Swagger is enabled)
if (swaggerEnabled)
{
    app.MapScalarApiReference(options =>
    {
        options
            .WithTitle("BeeLogistics API")
            .WithOpenApiRoutePattern("/swagger/{documentName}/swagger.json")
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
    Log.Information("Scalar API Reference mapped at /scalar");
}

Log.Information("Controllers and SignalR hubs mapped");

// Hangfire's static RecurringJob facade reads JobStorage.Current. AddHangfire only publishes the
// storage into DI — the static was being set as a side effect of UseHangfireDashboard, which was
// removed in 6613852 (2026-07-18). Every RecurringJob.AddOrUpdate below has thrown "Current
// JobStorage instance has not been initialized yet" ever since; the catch blocks downgraded it to a
// warning and startup continued, so the app looked healthy while no schedule was ever written.
//
// Nothing appeared broken because the Hangfire *server* keeps executing whatever rows storage
// already holds: jobs registered before that commit still fire on their old definitions, while
// every job added or re-scheduled since silently never existed. Six weeks of changes — the outbox
// dispatchers, Zammad retry, location retention, wallet upkeep, child-wallet withdrawal
// reconciliation and the PayMongo balance reconciler — were all no-ops.
//
// Resolving the storage from DI initialises the static before the registrations below use it.
// Hangfire's own advice is to inject IRecurringJobManager instead of the static facade; that is
// the better shape and worth doing, but it is ~15 mechanical rewrites and this restores every job
// in one line without touching them.
if (!runHangfireServer)
{
    // Skipped for the same reason the server is: AddOrUpdate would overwrite the owning
    // environment's job definitions with this build's, pointing them at types it may not have.
    Log.Warning("Skipping Hangfire recurring job registration (Hangfire:EnableServer=false).");
}
else
{

JobStorage.Current = app.Services.GetRequiredService<JobStorage>();

// --- CONFIGURE HANGFIRE RECURRING JOBS ---
// Schedule recurring jobs after middleware is configured (Hangfire will resolve from DI)
try
{
    Log.Information("Configuring Hangfire recurring jobs...");
    RecurringJob.AddOrUpdate<BookingBroadcastQueueService>(
        "process-expired-offers",
        service => service.ProcessExpiredOffersAsync(),
        "*/30 * * * * *", // Every 30 seconds (reduced DB load vs 10s)
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    RecurringJob.AddOrUpdate<BookingBroadcastQueueService>(
        "process-driver-queue",
        service => service.ProcessNextDriverInQueueAsync(),
        "*/30 * * * * *", // Every 30 seconds (reduced DB load vs 10s)
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Safety-net only. The real-time path is event-driven: offers are pushed to drivers on
    // creation (SignalR + push) and a driver reject immediately requests the next broadcast wave.
    // This pulse catches drivers who came online later and bookings whose initial broadcast
    // was missed. The per-booking NextPulseAt column (GetDueForPulseAsync) makes the underlying
    // query due-only, so ticking this frequently is cheap — it's what makes the 1-minute
    // per-booking pulse cadence (set in PulseBroadcastingBookingsAsync) actually happen on time
    // instead of being capped by a slow outer tick.
    RecurringJob.AddOrUpdate<BookingBroadcastQueueService>(
        "pulse-broadcasting-bookings",
        service => service.PulseBroadcastingBookingsAsync(),
        "*/15 * * * * *", // Every 15 seconds (due-only query keeps this cheap)
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Tickets whose inline Zammad sync timed out or failed are retried here with backoff.
    // Without it a Zammad outage silently dropped support tickets: they stayed local forever.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.CRM.Infrastructure.Services.ZammadTicketSyncService>(
        "zammad-ticket-sync",
        service => service.SyncPendingTicketsAsync(),
        "*/2 * * * *", // Every 2 minutes; per-ticket backoff decides what is actually due
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    RecurringJob.AddOrUpdate<DriversOutboxDispatcherService>(
        "drivers-outbox-dispatcher",
        service => service.ProcessPendingMessagesAsync(),
        "*/10 * * * * *", // Every 10 seconds: dispatch pending outbox messages
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Prunes delivered rows (the table had no retention at all and grew without bound) and
    // samples the standing stuck-message backlog, which the increment-only OutboxPoison counter
    // cannot report after a restart. See GitLab #65.
    RecurringJob.AddOrUpdate<DriversOutboxDispatcherService>(
        "drivers-outbox-prune",
        service => service.PruneAndReportAsync(),
        "*/15 * * * *", // Every 15 minutes
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Payment's own outbox (not MassTransit's - see the comment where the MassTransit outbox
    // registration was removed, near AddEntityFrameworkOutbox<BookingsDbContext>).
    RecurringJob.AddOrUpdate<PaymentOutboxDispatcherService>(
        "payment-outbox-dispatcher",
        service => service.ProcessPendingMessagesAsync(),
        "*/5 * * * * *", // Every 5 seconds: these drive wallet reversals and payment webhook state
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    RecurringJob.AddOrUpdate<PaymentOutboxDispatcherService>(
        "payment-outbox-prune",
        service => service.PruneAndReportAsync(),
        "*/15 * * * *", // Every 15 minutes
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    Log.Information("Hangfire recurring jobs configured");
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to configure Hangfire recurring jobs, continuing startup. Error: {Message}", ex.Message);
    // Don't crash if Hangfire jobs fail to configure
}

// --- HANGFIRE CLEANUP JOBS ---
try
{
    // Clean up old jobs daily at 2 AM UTC
    RecurringJob.AddOrUpdate<HangfireCleanupService>(
        "hangfire-cleanup-jobs",
        service => service.CleanupOldJobsAsync(),
        "0 2 * * *", // Daily at 2 AM UTC
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Clean up statistics weekly on Sunday at 3 AM UTC
    RecurringJob.AddOrUpdate<HangfireCleanupService>(
        "hangfire-cleanup-statistics",
        service => service.CleanupStatisticsAsync(),
        "0 3 * * 0", // Weekly on Sunday at 3 AM UTC
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
    Log.Information("Hangfire cleanup jobs configured");
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to configure Hangfire cleanup jobs, continuing startup. Error: {Message}", ex.Message);
}

// --- DRIVER TOP-UP OBSERVABILITY JOBS ---
// Reconciliation job: Safety net to catch payments missed by webhooks (e.g., webhook delivery failures, network issues).
// Only checks invoices older than 15 minutes and runs hourly to minimize Xendit API calls.
// This is necessary because webhooks can fail, and we need to ensure drivers' wallets are credited.
try
{
    RecurringJob.AddOrUpdate<DriverTopUpReconciliationService>(
        "driver-topup-reconcile",
        service => service.ReconcilePaidTopUpsAsync(),
        "*/30 * * * *", // every 30 minutes (reduced from 5min to reduce Xendit API calls)
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    RecurringJob.AddOrUpdate<DriverTopUpReconciliationService>(
        "driver-topup-expire-unpaid",
        service => service.ExpireUnpaidTopUpsAsync(),
        "*/15 * * * *", // every 15 minutes
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Withdrawals stuck in Approved because their transfer.* webhook never arrived. Without
    // this the driver's PendingPayout is held forever with nothing to notice it.
    RecurringJob.AddOrUpdate<WithdrawalReconciliationService>(
        "driver-withdrawal-reconcile",
        service => service.ReconcileWithdrawalsAsync(),
        "*/30 * * * *", // every 30 minutes; candidates must already be 20+ minutes old
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Child-wallet withdrawals whose transfer.outward.* webhook never arrived. PESONet stays in
    // flight for up to a banking day, so without this a driver's withdrawal sticks in Approved
    // while their mirror still shows money PayMongo has already sent.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.Drivers.Application.Services.ChildWalletWithdrawalReconciliationService>(
        "child-wallet-withdrawal-reconcile",
        service => service.ReconcileAsync(CancellationToken.None),
        "*/30 * * * *", // every 30 minutes; candidates must already be 20+ minutes old
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Settles BeePay <-> Cash Wallet transfers PayMongo accepted but had not finished. Runs often
    // because an unsettled leg does more than delay one transfer: a Pending row makes the balance
    // reconciler skip that wallet entirely, so leaving it unresolved retires the driver from every
    // automated correction we have.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.Drivers.Application.Services.WalletTransferReconciliationService>(
        "wallet-transfer-reconcile",
        service => service.ReconcileAsync(CancellationToken.None),
        "*/2 * * * *", // every 2 minutes
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Credits any balance PayMongo holds that our mirror has missed. Runs often because it is the
    // ONLY way a child-wallet top-up reaches us: PayMongo emits transaction events on the child
    // account, and only onboarding events reach the parent's webhook, so nothing else notices.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.Drivers.Application.Services.PayMongoBalanceReconciliationService>(
        "paymongo-balance-reconciliation",
        service => service.ReconcileAsync(CancellationToken.None),
        "*/10 * * * *", // every 10 minutes
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Monthly PayMongo wallet upkeep, recovered from the driver's Cash Wallet. Runs daily rather
    // than monthly on purpose: the charge is keyed per wallet per calendar month, so a daily run
    // is idempotent and picks up wallets activated mid-month or missed during an outage.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.Drivers.Application.Services.WalletUpkeepFeeService>(
        "driver-wallet-upkeep-fee",
        service => service.ChargeMonthlyUpkeepAsync(CancellationToken.None),
        "0 3 * * *", // 03:00 UTC daily
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Package-insurance policy expiry sweep (issue #104). Status-only: flips Active to Lapsed
    // once a coverage year runs out unpaid. Daily is more than enough for a date comparison.
    RecurringJob.AddOrUpdate<BeeLogistics.Modules.Drivers.Application.Services.DriverPackageInsuranceLapseService>(
        "driver-package-insurance-lapse-check",
        service => service.EnforceExpiryAsync(CancellationToken.None),
        "0 4 * * *", // 04:00 UTC daily
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    RecurringJob.AddOrUpdate<DriverTopUpReconciliationService>(
        "driver-wallet-negative-alerts",
        service => service.AlertNegativeWalletsAsync(),
        "*/10 * * * *", // every 10 minutes
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Surface booking-payment orphans and stuck refunds (logs + metrics for ops).
    RecurringJob.AddOrUpdate<PaymentReconciliationService>(
        "payment-reconcile",
        service => service.ReconcileAsync(),
        "0 * * * *", // hourly
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Age out the driver location trail. Daily and off-peak: it is a bulk delete on the
    // highest-volume table in the schema, which the live ingest path is writing to continuously.
    // --- Booking chat maintenance (#78) ---
    // All three no-op while Matrix:Enabled is false.
    RecurringJob.AddOrUpdate<BeeLogistics.Api.Services.BookingChatMaintenanceJob>(
        "booking-chat-freeze",
        job => job.FreezeAsync(),
        "*/30 * * * *", // every 30 minutes; the deadline is measured in hours, so this is timely enough
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // The backstop for bookings whose room provisioning never completed. Hourly: the live path
    // plus its retry schedule covers ~20 minutes, so anything still missing after an hour is
    // genuinely lost rather than in flight.
    RecurringJob.AddOrUpdate<BeeLogistics.Api.Services.BookingChatMaintenanceJob>(
        "booking-chat-backstop",
        job => job.BackstopAsync(),
        "17 * * * *", // offset from the hour so it does not pile onto the other hourly sweeps
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    // Bulk delete against Synapse, one admin call per room. Daily and off-peak.
    RecurringJob.AddOrUpdate<BeeLogistics.Api.Services.BookingChatMaintenanceJob>(
        "booking-chat-purge",
        job => job.PurgeAsync(),
        "40 3 * * *",
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    RecurringJob.AddOrUpdate<BeeLogistics.Api.Services.LocationHistoryRetentionJob>(
        "location-history-retention",
        job => job.PurgeAsync(),
        "30 3 * * *", // daily at 03:30 UTC
        new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}
catch (Exception ex)
{
    Log.Error(ex, "Failed to configure driver top-up observability jobs. Error: {Message}", ex.Message);
}

// Reads the recurring jobs back out of Hangfire and names them. Registration failures in the
// blocks above are caught and downgraded to warnings so startup continues, which means a job can
// go missing while the app looks entirely healthy: no error, a green deploy, and silence from a
// job that never runs. Asking storage what it actually holds is the only account of that which
// cannot drift from reality, and it costs one query at startup.
try
{
    using var jobConnection = JobStorage.Current.GetConnection();
    var registered = jobConnection.GetRecurringJobs().Select(j => j.Id).OrderBy(id => id).ToArray();
    Log.Information("Hangfire recurring jobs registered ({Count}): {Jobs}",
        registered.Length, string.Join(", ", registered));
}
catch (Exception ex)
{
    Log.Warning(ex, "Could not list Hangfire recurring jobs. Error: {Message}", ex.Message);
}

}

Log.Information("=== BeeLogistics API is ready to accept requests ===");
Log.Information("Listening on: {Urls}", app.Urls);

await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();                      // drains Serilog, pushing queued events into Sentry's worker
    SentrySdk.Flush(TimeSpan.FromSeconds(5)); // no-op when the hub is disabled
    SentrySdk.Close();
}
