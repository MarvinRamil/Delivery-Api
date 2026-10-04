namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Per-driver live-ingest state: how much update budget a driver has left, and where we last
/// accepted them. Shared across the whole process.
///
/// This exists because the state used to live in fields on <c>LocationSanitizer</c>, which is
/// registered scoped and resolved from a fresh scope for every single message. Both dictionaries
/// were therefore always empty on entry, so the rate limit and the teleport check silently never
/// ran - they were dead code that looked live. Holding the state here, as a singleton, is what
/// makes those two checks real.
///
/// Keeping it separate from the sanitizer rather than making the sanitizer a singleton is
/// deliberate: the sanitizer depends on the scoped IDistanceCalculationService, and a singleton
/// holding a scoped dependency captures a disposed DbContext.
/// </summary>
public interface IDriverLocationStateTracker
{
    /// <summary>
    /// Token-bucket admission control for live points.
    ///
    /// A bucket rather than a minimum interval because the driver app flushes its buffer with
    /// <c>Promise.all</c>, publishing up to BATCH_SIZE_LIMIT points to MQTT simultaneously. A hard
    /// one-per-second gate would drop all but the first of every flush - so the burst capacity has
    /// to exceed the client's batch size to leave normal operation untouched, while still capping
    /// a genuine flood.
    /// </summary>
    /// <returns>False when the driver is over budget and the point should be rejected.</returns>
    bool TryConsumeUpdateAllowance(Guid driverId, DateTime nowUtc, double refillPerSecond, int burstCapacity);

    /// <summary>
    /// The last accepted position for this driver, or null when there is none or it is older than
    /// <paramref name="maxAge"/>.
    ///
    /// The expiry matters: without it, a driver who goes offline, drives a long way, and comes back
    /// is measured against a stale position, fails the teleport check, and - because a rejected
    /// point never updates the baseline - stays permanently rejected. An expired baseline means we
    /// simply have no opinion, which is the honest answer.
    /// </summary>
    DriverLocationBaseline? GetBaseline(Guid driverId, DateTime nowUtc, TimeSpan maxAge);

    /// <summary>Records an accepted live point as the new baseline.</summary>
    void RecordAccepted(Guid driverId, decimal latitude, decimal longitude, DateTime recordedAtUtc, DateTime nowUtc);
}

public readonly record struct DriverLocationBaseline(decimal Latitude, decimal Longitude, DateTime RecordedAtUtc);
