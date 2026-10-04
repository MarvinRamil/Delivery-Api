using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Map.Application.Interfaces;

public interface ILocationRepository : IRepository<DriverLocation>
{
    Task<DriverLocation?> GetCurrentLocationByDriverIdAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>
    /// The driver's recorded trail from <c>LocationHistory</c>, newest first.
    /// </summary>
    /// <remarks>
    /// Reads <see cref="DriverLocationHistory"/>, not <see cref="DriverLocation"/>. The two are easy
    /// to confuse and this method used to query the wrong one: <c>DriverLocations</c> is a snapshot
    /// holding exactly one row per driver, updated in place, so the endpoint returned a single
    /// current position whatever range was asked for (GitLab #39).
    /// </remarks>
    /// <param name="limit">Maximum points returned, clamped. The trail grows at roughly 1,200 rows per hour per tracking driver, so an unbounded range is not safe to serve.</param>
    Task<IReadOnlyList<DriverLocationHistory>> GetLocationHistoryByDriverIdAsync(Guid driverId, DateTime? from = null, DateTime? to = null, int limit = 500, CancellationToken ct = default);

    /// <summary>
    /// Deletes trail points recorded before <paramref name="olderThanUtc"/>, up to
    /// <paramref name="maxRows"/> in one call.
    /// </summary>
    /// <returns>Rows deleted.</returns>
    Task<int> DeleteLocationHistoryOlderThanAsync(DateTime olderThanUtc, int maxRows, CancellationToken ct = default);
    Task<IReadOnlyList<DriverLocation>> GetLocationsByDriverIdsAsync(IEnumerable<Guid> driverIds, CancellationToken ct = default);
    Task<IReadOnlyList<DriverLocation>> GetLocationsWithinRadiusAsync(decimal centerLatitude, decimal centerLongitude, decimal radiusKm, CancellationToken ct = default);
}
