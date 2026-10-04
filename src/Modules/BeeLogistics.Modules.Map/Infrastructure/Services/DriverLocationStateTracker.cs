using System.Collections.Concurrent;
using BeeLogistics.Modules.Map.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <inheritdoc cref="IDriverLocationStateTracker"/>
public sealed class DriverLocationStateTracker : IDriverLocationStateTracker
{
    private sealed class DriverState
    {
        public double Tokens;
        public DateTime LastRefillUtc;
        public decimal Latitude;
        public decimal Longitude;
        public DateTime RecordedAtUtc;
        public bool HasBaseline;
        public DateTime LastSeenUtc;
    }

    private readonly ConcurrentDictionary<Guid, DriverState> _states = new();
    private readonly ILogger<DriverLocationStateTracker> _logger;

    /// <summary>
    /// Drivers idle for longer than this are forgotten. Now that this state is process-lifetime
    /// rather than per-message, an unbounded dictionary keyed by driver id would grow for every
    /// driver ever seen. Forgetting a driver is harmless - they simply start with a full bucket and
    /// no baseline, which is the same position a freshly started process is in.
    /// </summary>
    private readonly TimeSpan _retention;

    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastSweepUtc = DateTime.MinValue;
    private readonly object _sweepLock = new();

    public DriverLocationStateTracker(IConfiguration configuration, ILogger<DriverLocationStateTracker> logger)
    {
        _logger = logger;
        _retention = TimeSpan.FromMinutes(
            int.TryParse(configuration["Location:StateRetentionMinutes"], out var minutes) && minutes > 0
                ? minutes
                : 60);
    }

    public bool TryConsumeUpdateAllowance(Guid driverId, DateTime nowUtc, double refillPerSecond, int burstCapacity)
    {
        if (burstCapacity <= 0)
            return true;

        Sweep(nowUtc);

        var state = _states.GetOrAdd(driverId, _ => new DriverState
        {
            Tokens = burstCapacity,
            LastRefillUtc = nowUtc,
            LastSeenUtc = nowUtc
        });

        // Per-driver lock. Contention is limited to concurrent messages for the same driver, which
        // is exactly the case the bucket is arbitrating.
        lock (state)
        {
            var elapsedSeconds = Math.Max(0, (nowUtc - state.LastRefillUtc).TotalSeconds);
            state.Tokens = Math.Min(burstCapacity, state.Tokens + (elapsedSeconds * refillPerSecond));
            state.LastRefillUtc = nowUtc;
            state.LastSeenUtc = nowUtc;

            if (state.Tokens < 1d)
                return false;

            state.Tokens -= 1d;
            return true;
        }
    }

    public DriverLocationBaseline? GetBaseline(Guid driverId, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!_states.TryGetValue(driverId, out var state))
            return null;

        lock (state)
        {
            if (!state.HasBaseline)
                return null;

            if (nowUtc - state.RecordedAtUtc > maxAge)
                return null;

            return new DriverLocationBaseline(state.Latitude, state.Longitude, state.RecordedAtUtc);
        }
    }

    public void RecordAccepted(Guid driverId, decimal latitude, decimal longitude, DateTime recordedAtUtc, DateTime nowUtc)
    {
        var state = _states.GetOrAdd(driverId, _ => new DriverState
        {
            Tokens = 0,
            LastRefillUtc = nowUtc,
            LastSeenUtc = nowUtc
        });

        lock (state)
        {
            // Out-of-order delivery is normal on a burst flush; keep the newest reading as the
            // baseline so a late-arriving older point cannot rewind it.
            if (state.HasBaseline && recordedAtUtc < state.RecordedAtUtc)
            {
                state.LastSeenUtc = nowUtc;
                return;
            }

            state.Latitude = latitude;
            state.Longitude = longitude;
            state.RecordedAtUtc = recordedAtUtc;
            state.HasBaseline = true;
            state.LastSeenUtc = nowUtc;
        }
    }

    private void Sweep(DateTime nowUtc)
    {
        if (nowUtc - _lastSweepUtc < _sweepInterval)
            return;

        lock (_sweepLock)
        {
            if (nowUtc - _lastSweepUtc < _sweepInterval)
                return;

            _lastSweepUtc = nowUtc;
        }

        var removed = 0;
        foreach (var (driverId, state) in _states)
        {
            DateTime lastSeen;
            lock (state)
            {
                lastSeen = state.LastSeenUtc;
            }

            if (nowUtc - lastSeen > _retention && _states.TryRemove(driverId, out _))
                removed++;
        }

        if (removed > 0)
            _logger.LogDebug("[LOCATION] Evicted {Count} idle driver location state entries", removed);
    }
}
