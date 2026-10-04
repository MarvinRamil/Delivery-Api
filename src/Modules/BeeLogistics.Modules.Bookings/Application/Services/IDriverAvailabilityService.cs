using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Service for determining driver availability for bookings.
/// Encapsulates business logic for filtering available drivers.
/// </summary>
public interface IDriverAvailabilityService
{
    /// <summary>
    /// Gets available drivers for a booking based on various criteria:
    /// - Driver is active
    /// - Driver has no active dispatches
    /// - Driver has appropriate vehicle type
    /// - Driver is within reasonable distance (optional)
    /// </summary>
    /// <param name="bookingId">The booking ID to find drivers for</param>
    /// <param name="smallVehicleTypes">List of small vehicle types that qualify</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of available drivers with their distances (if calculable)</returns>
    Task<IReadOnlyList<AvailableDriver>> GetAvailableDriversAsync(
        Guid bookingId,
        IReadOnlyList<string> smallVehicleTypes,
        CancellationToken ct = default);
}

/// <summary>
/// Represents an available driver with their distance to the booking pickup location.
/// <para>
/// <see cref="Score"/> is the ranking signal (higher = offer sooner). Proximity is the base;
/// <see cref="RankingScore"/> composes a mode-specific bias on top of it.
/// </para>
/// </summary>
public record AvailableDriver(
    Guid DriverId,
    decimal? DistanceKm,
    int SequenceNumber,
    double Score = 0
)
{
    /// <summary>
    /// Proximity score in (0,1]: closest drivers score highest, drivers with no known
    /// location score 0 (offered last). Centralised so all candidate paths rank identically.
    /// </summary>
    public static double ProximityScore(decimal? distanceKm)
        => distanceKm.HasValue ? 1.0 / (1.0 + (double)distanceKm.Value) : 0.0;

    /// <summary>
    /// The full ranking signal: proximity plus a mode-specific bias.
    /// </summary>
    /// <param name="distanceKm">Distance to pickup, or null when the driver reports no location.</param>
    /// <param name="bonus">
    /// The bias, already normalised to <c>[0,1]</c> and weighted by the caller. Zero for
    /// <see cref="DeliveryMode.Regular"/> and for any mode whose weight is unconfigured.
    /// </param>
    /// <remarks>
    /// A bonus of exactly 0 makes this <b>identical</b> to <see cref="ProximityScore"/> — not
    /// approximately, but bit-for-bit, since adding zero to a finite double is exact. That is what
    /// makes "Regular ranking is unchanged" something a reader can verify rather than trust, and it
    /// is why the bias ships weighted at 0 rather than behind a boolean flag.
    ///
    /// Calibration: <see cref="ProximityScore"/> is <c>1/(1+km)</c>, so at typical urban distances
    /// a bonus of 0.35 is worth roughly "3 km closer". Raise weights slowly and watch pickup
    /// times — over-weighting makes the market prefer convenient drivers over near ones.
    /// </remarks>
    public static double RankingScore(decimal? distanceKm, double bonus = 0)
        => ProximityScore(distanceKm) + bonus;
}
