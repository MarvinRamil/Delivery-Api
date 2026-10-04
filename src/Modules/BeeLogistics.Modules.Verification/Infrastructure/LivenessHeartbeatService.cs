using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

/// <summary>
/// Periodically logs whether the YOLO liveness service is reachable (like MQTT heartbeat).
/// </summary>
public sealed class LivenessHeartbeatService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LivenessHeartbeatService> _logger;
    private readonly LivenessOptions _options;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public LivenessHeartbeatService(
        IServiceScopeFactory scopeFactory,
        ILogger<LivenessHeartbeatService> logger,
        IOptions<LivenessOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Liveness] Heartbeat service started (interval: {Interval}s)", Interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.BaseUrl))
                {
                    var baseUrl = _options.BaseUrl.TrimEnd('/');
                    bool reachable = false;
                    string? lastError = null;

                    for (int attempt = 1; attempt <= MaxRetries; attempt++)
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var client = scope.ServiceProvider.GetRequiredService<ILivenessApiClient>();
                            var (configured, ok, error) = await client.CheckHealthAsync(stoppingToken);
                            if (ok)
                            {
                                reachable = true;
                                if (attempt > 1)
                                    _logger.LogInformation("[Liveness] Heartbeat: connected to YOLO service at {BaseUrl} (succeeded on attempt {Attempt})", baseUrl, attempt);
                                break;
                            }
                            lastError = error;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex.Message;
                            if (attempt < MaxRetries)
                                _logger.LogDebug("[Liveness] Heartbeat attempt {Attempt}/{Max} failed: {Error}, retrying in {Delay}s", attempt, MaxRetries, ex.Message, RetryDelay.TotalSeconds);
                        }

                        if (attempt < MaxRetries)
                            await Task.Delay(RetryDelay, stoppingToken);
                    }

                    if (reachable)
                    {
                        if (lastError == null) // first attempt succeeded
                            _logger.LogInformation("[Liveness] Heartbeat: connected to YOLO service at {BaseUrl}", baseUrl);
                    }
                    else
                    {
                        _logger.LogWarning("[Liveness] Heartbeat: YOLO service not reachable at {BaseUrl} after {Attempts} attempts, error: {Error}",
                            baseUrl, MaxRetries, lastError ?? "unknown");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Liveness] Heartbeat: failed to check YOLO service");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("[Liveness] Heartbeat service stopped");
    }
}
