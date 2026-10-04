namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// How a driver has recently responded to offers: how many they were sent, and how many they took.
/// </summary>
public readonly record struct OfferResponseStats(int Offered, int Accepted);

/// <summary>
/// Turns recent offer responsiveness into a ranking bias in <c>[0,1]</c>, so an urgent booking can
/// be offered first to drivers who actually take jobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acceptance rate, not star rating.</b> Ratings live in the Rating module, which Bookings does
/// not reference — Rating references Bookings — and <c>IDriverRatingRepository</c> has no batch
/// method, so a rating bias would cost a new <c>Shared/Contracts</c> interface, a new repository
/// method and cross-module DI ordering. Acceptance rate is available in-module in one grouped query,
/// and is arguably the better signal anyway: the question being asked here is "will this driver take
/// the job", not "were customers happy last month".
/// </para>
/// <para>
/// It is also the fairer signal to rank on. A driver's rating reflects things partly outside their
/// control; whether they answer offers is a direct choice.
/// </para>
/// </remarks>
public static class DriverQualityScore
{
    /// <summary>
    /// Offers below which a driver's rate is not yet meaningful and the neutral score is used.
    /// </summary>
    public const int DefaultMinSamples = 5;

    /// <summary>
    /// What a driver scores when there is not enough history to judge them.
    /// </summary>
    /// <remarks>
    /// Mid-range on purpose. Scoring an unknown driver 0 would rank every new driver behind every
    /// established one on their first shift — they would see the worst work, accept less, and stay
    /// unknown. Scoring them 1 would hand new accounts the best work and make the bias gameable.
    /// </remarks>
    public const double NeutralScore = 0.5;

    /// <summary>
    /// Acceptance rate in <c>[0,1]</c>, or <see cref="NeutralScore"/> below
    /// <paramref name="minSamples"/> offers.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted: <c>Accepted</c> and <c>Offered</c> come from a grouped query
    /// over rows a re-broadcast can add to, and a rate above 1 would silently outrank proximity
    /// entirely once weighted.
    /// </remarks>
    public static double From(OfferResponseStats stats, int minSamples = DefaultMinSamples)
    {
        if (stats.Offered < minSamples || stats.Offered <= 0)
            return NeutralScore;

        return Math.Clamp((double)stats.Accepted / stats.Offered, 0.0, 1.0);
    }
}
