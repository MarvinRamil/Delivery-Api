using BeeLogistics.Modules.Verification.Application.DTOs;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class LivenessSession
{
    public string SessionId { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public string[] Directions { get; set; } = Array.Empty<string>();
    public HashSet<string> DirectionsPassed { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public interface ILivenessSessionStore
{
    LivenessSession? Get(string sessionId);
    void Set(LivenessSession session);
    void Remove(string sessionId);
    void RemoveExpired();
}

public class InMemoryLivenessSessionStore : ILivenessSessionStore
{
    private readonly Dictionary<string, LivenessSession> _sessions = new();
    private readonly TimeSpan _expiry;
    private readonly Timer _cleanupTimer;

    public InMemoryLivenessSessionStore(IOptions<LivenessOptions> options)
    {
        _expiry = TimeSpan.FromMinutes(options.Value.SessionExpiryMinutes);
        _cleanupTimer = new Timer(_ => RemoveExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public LivenessSession? Get(string sessionId)
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(sessionId, out var s))
                return null;
            if (DateTime.UtcNow > s.ExpiresAt)
            {
                _sessions.Remove(sessionId);
                return null;
            }
            return s;
        }
    }

    public void Set(LivenessSession session)
    {
        lock (_sessions)
            _sessions[session.SessionId] = session;
    }

    public void Remove(string sessionId)
    {
        lock (_sessions)
            _sessions.Remove(sessionId);
    }

    public void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        lock (_sessions)
        {
            foreach (var id in _sessions.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList())
                _sessions.Remove(id);
        }
    }

    public static string NewSessionId() => Guid.NewGuid().ToString("N");
}
