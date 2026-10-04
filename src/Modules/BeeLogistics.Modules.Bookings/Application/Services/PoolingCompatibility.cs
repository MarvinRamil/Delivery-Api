namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>A point on the map. Decimal to match how coordinates are stored and carried.</summary>
public readonly record struct GeoPoint(decimal Latitude, decimal Longitude);

/// <summary>
/// Scores how well a booking fits into what a driver is already carrying, so a pooled booking can
/// be offered first to a driver who is already heading that way.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to answer a specific objection.</b> <see cref="DriverCapacityPolicy"/> declines
/// server-side batching, and says why: <i>"distance alone cannot tell a good batch from a bad one —
/// two pickups 2 km apart heading in opposite directions look identical to one that is on the way —
/// and the driver … is far better placed to decide."</i>
/// </para>
/// <para>
/// That is correct, and it is about <i>distance</i>. So this does not score distance. It scores the
/// <b>marginal detour</b> against the leg the driver already owes, which is precisely the
/// discrimination the objection says distance cannot make: the opposite-direction pair scores ~0
/// and the on-the-way pair scores ~1, even though the two pickups are equally far away.
/// </para>
/// <para>
/// And the score only decides <b>who is asked first</b>. It never filters a driver out, never
/// auto-assigns, and never relaxes a cap. The driver still sees every address and both fares and
/// still decides. The worst case is a suboptimal offer <i>order</i>, corrected by the next pulse
/// wave — it cannot produce a wrong answer for a customer.
/// </para>
/// <para>
/// <b>Pure in-process Haversine, deliberately.</b> Not <c>IDistanceCalculationService</c> (a PostGIS
/// round trip) and not a routing API: this runs inside candidate selection, on a 15-second Hangfire
/// tick, across every due booking, for up to 45 candidates each. <c>RouteDistanceCalculationService</c>
/// is already in-process Haversine, so this matches it. Straight-line error is acceptable because the
/// output is an ordering, not a fare.
/// </para>
/// </remarks>
public static class PoolingCompatibility
{
    /// <summary>Mean Earth radius in km, as used by <c>RouteDistanceCalculationService</c>.</summary>
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// The extra distance a driver covers by interleaving this booking into a leg they already owe.
    /// </summary>
    /// <param name="driver">Where the driver is now.</param>
    /// <param name="pickup">The candidate booking's pickup.</param>
    /// <param name="dropoff">The candidate booking's final dropoff.</param>
    /// <param name="nextStop">The next stop the driver still owes on work they already hold.</param>
    /// <remarks>
    /// <code>
    /// viaB   = |driver→pickup| + |pickup→dropoff| + |dropoff→nextStop|
    /// direct = |driver→nextStop|
    /// detour = viaB - direct - |pickup→dropoff|
    /// </code>
    /// The candidate's own pickup-to-dropoff leg is subtracted because the driver has to cover it
    /// either way — it is the price of the job, not of the batching. What is left is the cost of
    /// interleaving, so a job directly on the driver's path detours ~0 km and an opposite-direction
    /// job detours roughly twice the distance to it.
    ///
    /// Never negative: the triangle inequality guarantees it mathematically, but floating-point
    /// subtraction of near-equal sums does not, and a small negative would score above a perfect
    /// on-the-way match.
    /// </remarks>
    public static double DetourKm(GeoPoint driver, GeoPoint pickup, GeoPoint dropoff, GeoPoint nextStop)
    {
        var legPickupToDropoff = HaversineKm(pickup, dropoff);
        var viaB = HaversineKm(driver, pickup) + legPickupToDropoff + HaversineKm(dropoff, nextStop);
        var direct = HaversineKm(driver, nextStop);

        var detour = viaB - direct - legPickupToDropoff;
        return detour > 0 ? detour : 0;
    }

    /// <summary>
    /// A detour turned into a bias in <c>[0,1]</c>: <c>clamp(1 - detourKm / maxDetourKm, 0, 1)</c>.
    /// Monotonically decreasing, and 0 once the detour reaches <paramref name="maxDetourKm"/>.
    /// </summary>
    /// <remarks>
    /// A non-positive <paramref name="maxDetourKm"/> disables the bias rather than dividing by zero —
    /// clearing the config value is a legitimate way to turn pooling scoring off.
    /// </remarks>
    public static double Score(double detourKm, double maxDetourKm)
    {
        if (maxDetourKm <= 0)
            return 0;

        var score = 1.0 - (detourKm / maxDetourKm);
        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// The best fit across every leg the driver already owes, or <b>0</b> when they owe none.
    /// </summary>
    /// <remarks>
    /// Best-of rather than average: a driver holding one job on the way and one in the opposite
    /// direction is still a good candidate, because they can interleave the compatible one.
    ///
    /// An empty list scores 0 — <b>un-boosted, not penalised</b>. An idle driver right next to the
    /// pickup must stay competitive with a busy driver who happens to be heading past, or "pooling"
    /// would quietly become "always prefer busy drivers".
    /// </remarks>
    public static double BestScore(
        GeoPoint driver,
        GeoPoint pickup,
        GeoPoint dropoff,
        IReadOnlyList<GeoPoint> heldNextStops,
        double maxDetourKm)
    {
        if (heldNextStops.Count == 0)
            return 0;

        var best = 0.0;
        foreach (var nextStop in heldNextStops)
        {
            var score = Score(DetourKm(driver, pickup, dropoff, nextStop), maxDetourKm);
            if (score > best)
                best = score;
        }

        return best;
    }

    /// <summary>Great-circle distance in km.</summary>
    public static double HaversineKm(GeoPoint a, GeoPoint b)
    {
        var lat1 = ToRadians((double)a.Latitude);
        var lat2 = ToRadians((double)b.Latitude);
        var deltaLat = lat2 - lat1;
        var deltaLon = ToRadians((double)b.Longitude - (double)a.Longitude);

        var h = (Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2))
            + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2));

        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
