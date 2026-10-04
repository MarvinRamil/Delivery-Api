using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// In-process ICacheService used when Redis is not configured. Tracks keys so
/// RemoveByPrefixAsync actually works (IMemoryCache has no key enumeration).
/// Single-instance only: do not use in multi-replica deployments.
/// </summary>
public class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ConcurrentDictionary<string, byte> _keys = new();

    public MemoryCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        _cache.TryGetValue(key, out T? value);
        return Task.FromResult(value);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(10)
        };
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) => _keys.TryRemove((string)evictedKey, out _));

        _cache.Set(key, value, options);
        _keys.TryAdd(key, 0);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        _keys.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (var key in _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            _cache.Remove(key);
            _keys.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}
