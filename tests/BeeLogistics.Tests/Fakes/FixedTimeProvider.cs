namespace BeeLogistics.Tests.Fakes;

/// <summary>TimeProvider frozen at a fixed instant, for deterministic clock-dependent tests.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
