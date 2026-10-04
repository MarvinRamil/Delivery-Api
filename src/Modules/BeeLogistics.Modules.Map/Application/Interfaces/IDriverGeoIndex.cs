namespace BeeLogistics.Modules.Map.Application.Interfaces;

/// <summary>
/// Uber H3-based geospatial index of online driver positions.
/// Drivers are bucketed into H3 hexagonal cells so candidate lookup for a pickup
/// point is a handful of O(1) cell reads (expanding rings) instead of a scan
/// over all drivers. Entries are timestamped and ignored after a staleness
/// window, so drivers that stop reporting age out without explicit removal.
/// </summary>
public interface IDriverGeoIndex
{
    /// <summary>
    /// Adds or moves a driver in the index. Called on every accepted location update.
    /// </summary>
    Task IndexDriverAsync(Guid driverId, decimal latitude, decimal longitude, CancellationToken ct = default);

    /// <summary>
    /// Finds candidate drivers near a point by expanding H3 rings around the
    /// origin cell until at least <paramref name="maxResults"/> fresh candidates
    /// are found (or the configured/overridden max ring is reached). Returns
    /// candidates with great-circle distance to the search point, closest first.
    /// </summary>
    /// <param name="maxRingsOverride">
    /// Optional per-call ring cap (e.g. a progressively widening search radius as
    /// a booking ages). Falls back to the configured default when null. Always
    /// clamped to the implementation's hard safety cap regardless of this value.
    /// </param>
    Task<IReadOnlyList<DriverGeoCandidate>> FindNearestDriversAsync(
        decimal latitude,
        decimal longitude,
        int maxResults,
        int? maxRingsOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a driver from the index (e.g. driver goes offline or is deactivated).
    /// </summary>
    Task RemoveDriverAsync(Guid driverId, CancellationToken ct = default);
}

/// <summary>
/// A driver found in the geo index. DistanceKm is the great-circle distance
/// from the queried point to the driver's last known position.
/// </summary>
public sealed record DriverGeoCandidate(Guid DriverId, decimal Latitude, decimal Longitude, double DistanceKm);
