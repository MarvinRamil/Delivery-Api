using BeeLogistics.Modules.Bookings.Application.Services;
using Xunit;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The On-Demand responsiveness bias.
///
/// The behaviour worth guarding is what happens to a driver we know nothing about. Scoring an
/// unknown driver 0 would rank every new driver behind every established one on their first shift:
/// they would be offered the worst work, accept less of it, and stay unknown. That is a ranking
/// function that decides someone's income, so the neutral default is the point of this file.
/// </summary>
public class DriverQualityScoreTests
{
    [Fact]
    public void A_driver_with_enough_history_scores_their_acceptance_rate()
    {
        Assert.Equal(0.8, DriverQualityScore.From(new OfferResponseStats(Offered: 10, Accepted: 8)));
    }

    [Fact]
    public void A_driver_with_too_little_history_scores_neutral_rather_than_zero()
    {
        // Four offers, none accepted — not yet evidence of anything.
        Assert.Equal(
            DriverQualityScore.NeutralScore,
            DriverQualityScore.From(new OfferResponseStats(4, 0), minSamples: 5));
    }

    [Fact]
    public void A_driver_with_no_history_at_all_scores_neutral()
    {
        // This is what a brand-new driver looks like: absent from the grouped query entirely, so
        // the caller passes default(OfferResponseStats).
        Assert.Equal(DriverQualityScore.NeutralScore, DriverQualityScore.From(default));
    }

    [Fact]
    public void The_sample_floor_is_exclusive_at_the_boundary()
    {
        // Exactly minSamples is enough; one fewer is not.
        Assert.Equal(0.0, DriverQualityScore.From(new OfferResponseStats(5, 0), minSamples: 5));
        Assert.Equal(
            DriverQualityScore.NeutralScore,
            DriverQualityScore.From(new OfferResponseStats(4, 0), minSamples: 5));
    }

    [Fact]
    public void A_driver_who_takes_everything_scores_one_and_one_who_takes_nothing_scores_zero()
    {
        Assert.Equal(1.0, DriverQualityScore.From(new OfferResponseStats(20, 20)));
        Assert.Equal(0.0, DriverQualityScore.From(new OfferResponseStats(20, 0)));
    }

    [Fact]
    public void An_impossible_rate_is_clamped_rather_than_trusted()
    {
        // Counts come from a grouped query over rows a re-broadcast can add to. A rate above 1
        // would silently outrank proximity entirely once weighted.
        Assert.Equal(1.0, DriverQualityScore.From(new OfferResponseStats(5, 50)));
    }

    [Fact]
    public void The_score_always_lands_in_the_unit_interval()
    {
        // The weight calibration in AvailableDriver.RankingScore assumes [0,1]; anything outside it
        // makes the configured weight mean something other than what it says.
        foreach (var offered in new[] { 0, 1, 5, 100 })
            foreach (var accepted in new[] { 0, 1, 5, 100 })
                Assert.InRange(DriverQualityScore.From(new OfferResponseStats(offered, accepted)), 0.0, 1.0);
    }
}
