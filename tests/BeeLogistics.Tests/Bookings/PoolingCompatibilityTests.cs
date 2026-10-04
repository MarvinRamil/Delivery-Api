using BeeLogistics.Modules.Bookings.Application.Services;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The pooling detour score.
///
/// <see cref="DriverCapacityPolicy"/> declines server-side batching and explains why: <i>"distance
/// alone cannot tell a good batch from a bad one — two pickups 2 km apart heading in opposite
/// directions look identical to one that is on the way"</i>. This score exists to answer exactly
/// that, so the load-bearing test in this file is
/// <see cref="Two_pickups_the_same_distance_away_score_very_differently"/> — the literal pair the
/// doc says distance cannot separate. If that one ever stops holding, the bias is scoring distance
/// again and should be switched off rather than tuned.
///
/// Coordinates are laid out on a rough grid around Manila: 0.01° of latitude is ~1.1 km, and at
/// this latitude 0.01° of longitude is ~1.08 km. Close enough that a "2 km apart" claim in a test
/// name is honest.
/// </summary>
public class PoolingCompatibilityTests
{
    private const double MaxDetourKm = 4.0;

    /// <summary>A point offset from the depot by whole kilometres, roughly.</summary>
    private static GeoPoint At(double kmNorth, double kmEast)
        => new(14.5995m + (decimal)(kmNorth / 111.0), 120.9842m + (decimal)(kmEast / 107.6));

    // --- The objection ---

    [Fact]
    public void Two_pickups_the_same_distance_away_score_very_differently()
    {
        // The driver is at the origin and already owes a stop 6 km north.
        var driver = At(0, 0);
        var nextStop = At(6, 0);

        // Both candidate bookings pick up 2 km from the driver. One continues north, along the way.
        var onTheWay = PoolingCompatibility.Score(
            PoolingCompatibility.DetourKm(driver, At(2, 0), At(4, 0), nextStop), MaxDetourKm);

        // The other picks up 2 km south and delivers further south — the opposite direction.
        var opposite = PoolingCompatibility.Score(
            PoolingCompatibility.DetourKm(driver, At(-2, 0), At(-4, 0), nextStop), MaxDetourKm);

        Assert.True(onTheWay > 0.9, $"on-the-way should score near 1, got {onTheWay:F3}");
        Assert.Equal(0, opposite);

        // Stated as the comparison the doc says is impossible on distance alone.
        Assert.True(onTheWay > opposite);
    }

    [Fact]
    public void A_booking_directly_on_the_drivers_path_costs_almost_no_detour()
    {
        var driver = At(0, 0);
        var detour = PoolingCompatibility.DetourKm(driver, At(2, 0), At(4, 0), At(6, 0));

        Assert.True(detour < 0.05, $"expected ~0 km of detour, got {detour:F3}");
    }

    [Fact]
    public void A_booking_in_the_opposite_direction_costs_the_whole_round_trip_away_from_the_route()
    {
        // Driver at 0 owes a stop 6 km north. The candidate picks up 2 km south and drops at 4 km
        // south, so the driver ends up 4 km the wrong way and has to come all the way back:
        //   viaB   = 2 (to pickup) + 2 (the job) + 10 (dropoff back up to the held stop) = 14
        //   direct = 6
        //   detour = 14 - 6 - 2 (the job, which is owed either way) = 6
        var detour = PoolingCompatibility.DetourKm(At(0, 0), At(-2, 0), At(-4, 0), At(6, 0));

        Assert.InRange(detour, 5.5, 6.5);
    }

    // --- Score shape ---

    [Fact]
    public void The_score_falls_monotonically_as_the_detour_grows()
    {
        var previous = double.MaxValue;
        for (var detour = 0.0; detour <= MaxDetourKm; detour += 0.5)
        {
            var score = PoolingCompatibility.Score(detour, MaxDetourKm);
            Assert.True(score <= previous, $"score rose at detour {detour}");
            previous = score;
        }
    }

    [Fact]
    public void A_zero_detour_scores_one_and_the_ceiling_scores_zero()
    {
        Assert.Equal(1.0, PoolingCompatibility.Score(0, MaxDetourKm));
        Assert.Equal(0.0, PoolingCompatibility.Score(MaxDetourKm, MaxDetourKm));
    }

    [Fact]
    public void A_detour_beyond_the_ceiling_clamps_to_zero_rather_than_going_negative()
    {
        // A negative bonus would push a driver below someone with no bonus at all, turning a
        // "prefer this driver" signal into a penalty nobody asked for.
        Assert.Equal(0.0, PoolingCompatibility.Score(500, MaxDetourKm));
    }

    [Fact]
    public void A_non_positive_ceiling_disables_the_bias_instead_of_dividing_by_zero()
    {
        // Clearing MaxDetourKm in config is a legitimate way to turn pooling scoring off.
        Assert.Equal(0.0, PoolingCompatibility.Score(1.0, 0));
        Assert.Equal(0.0, PoolingCompatibility.Score(1.0, -5));
    }

    [Fact]
    public void The_detour_is_never_negative()
    {
        // The triangle inequality guarantees this mathematically; subtracting near-equal sums of
        // doubles does not. A small negative would score above a perfect on-the-way match.
        var driver = At(0, 0);
        var detour = PoolingCompatibility.DetourKm(driver, driver, driver, driver);

        Assert.True(detour >= 0);
    }

    // --- Best across held legs ---

    [Fact]
    public void A_driver_holding_nothing_gets_no_bonus_and_no_penalty()
    {
        // An idle driver right next to the pickup has to stay competitive, or "pooling" quietly
        // becomes "always prefer busy drivers".
        var score = PoolingCompatibility.BestScore(
            At(0, 0), At(2, 0), At(4, 0), Array.Empty<GeoPoint>(), MaxDetourKm);

        Assert.Equal(0, score);
    }

    [Fact]
    public void The_best_compatible_leg_wins_rather_than_the_average()
    {
        // A driver holding one job on the way and one in the opposite direction is still a good
        // candidate: they can interleave the compatible one. Averaging would hide that.
        var driver = At(0, 0);
        var legs = new[] { At(-8, 0), At(6, 0) };  // one wrong way, one on the way

        var best = PoolingCompatibility.BestScore(driver, At(2, 0), At(4, 0), legs, MaxDetourKm);
        var onlyBadLeg = PoolingCompatibility.BestScore(driver, At(2, 0), At(4, 0), [At(-8, 0)], MaxDetourKm);

        Assert.True(best > 0.9, $"expected the compatible leg to win, got {best:F3}");
        Assert.Equal(0, onlyBadLeg);
    }

    [Fact]
    public void A_perpendicular_job_scores_between_on_the_way_and_opposite()
    {
        // Sanity that the metric is a gradient and not a two-value switch.
        var driver = At(0, 0);
        var nextStop = At(6, 0);

        var sideways = PoolingCompatibility.Score(
            PoolingCompatibility.DetourKm(driver, At(0, 1.5), At(1.5, 1.5), nextStop), MaxDetourKm);

        Assert.InRange(sideways, 0.01, 0.99);
    }

    // --- Distance primitive ---

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 111.19)]     // one degree of latitude
    public void Haversine_matches_known_distances(
        double lat1, double lon1, double lat2, double lon2, double expectedKm)
    {
        var actual = PoolingCompatibility.HaversineKm(
            new GeoPoint((decimal)lat1, (decimal)lon1),
            new GeoPoint((decimal)lat2, (decimal)lon2));

        Assert.InRange(actual, expectedKm - 0.5, expectedKm + 0.5);
    }

    [Fact]
    public void Haversine_is_symmetric()
    {
        var a = At(0, 0);
        var b = At(3, 4);

        Assert.Equal(
            PoolingCompatibility.HaversineKm(a, b),
            PoolingCompatibility.HaversineKm(b, a),
            precision: 9);
    }
}
