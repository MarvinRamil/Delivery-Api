using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using System.Text.Json;

namespace BeeLogistics.Shared.Infrastructure;

public class RedisCacheService : ICacheService
{
    /// <summary>
    /// Key prefix applied by AddStackExchangeRedisCache (options.InstanceName).
    /// Program.cs uses this same constant so SCAN-based prefix deletion matches
    /// the keys IDistributedCache actually writes.
    /// </summary>
    public const string InstanceName = "BeeLogistics:";

    private readonly IDistributedCache _cache;
    private readonly IConnectionMultiplexer? _redis;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RedisCacheService(IDistributedCache cache, IConnectionMultiplexer? redis = null)
    {
        _cache = cache;
        _redis = redis;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetStringAsync(key, cancellationToken);
        if (string.IsNullOrEmpty(cached))
            return default;

        return JsonSerializer.Deserialize<T>(cached, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(10)
        };

        var serialized = JsonSerializer.Serialize(value, JsonOptions);
        await _cache.SetStringAsync(key, serialized, options, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        await _cache.RemoveAsync(key, cancellationToken);
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (_redis == null)
            throw new InvalidOperationException(
                "RemoveByPrefixAsync requires a Redis connection (IConnectionMultiplexer). " +
                "Without one, prefix invalidation would silently leave stale entries.");

        var db = _redis.GetDatabase();
        var pattern = InstanceName + prefix + "*";

        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (server.IsReplica)
                continue;

            var batch = new List<RedisKey>(500);
            await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: 500).WithCancellation(cancellationToken))
            {
                batch.Add(key);
                if (batch.Count >= 500)
                {
                    await db.KeyDeleteAsync(batch.ToArray());
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await db.KeyDeleteAsync(batch.ToArray());
        }
    }
}
