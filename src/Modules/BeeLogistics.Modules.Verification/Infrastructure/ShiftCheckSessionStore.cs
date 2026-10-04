using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

/// <summary>
/// Separate in-memory store for shift-check sessions so their IDs are never
/// interchangeable with onboarding liveness sessions.
/// </summary>
public interface IShiftCheckSessionStore : ILivenessSessionStore
{
}

public class InMemoryShiftCheckSessionStore : InMemoryLivenessSessionStore, IShiftCheckSessionStore
{
    public InMemoryShiftCheckSessionStore(IOptions<LivenessOptions> options) : base(options)
    {
    }
}
