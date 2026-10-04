using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Readiness check for Redis (cache, H3 geo index, SignalR backplane).
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await _redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"Redis ping {latency.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis unreachable", ex);
        }
    }
}
