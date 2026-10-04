using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Computes how long a driver offer stays open. Adaptive: when many drivers are
/// available the window is short (fast hand-off to the next driver); when few are
/// available it's longer (don't burn through a thin supply). Values come from
/// <c>BookingSettings:OfferExpiration{Many,Few}DriversMinutes</c>, overridable per
/// delivery mode under <c>BookingSettings:DeliveryModes:{mode}</c>.
/// </summary>
public static class OfferExpiry
{
    /// <summary>
    /// The floor on an offer's lifetime. Deliberately not 0: two separate expiry paths
    /// (<c>GetExpiredOffersAsync</c> and <c>ProcessNextDriverInQueueAsync</c>) apply a 30-second
    /// grace before treating an offer as expired, so a 1-minute TTL already leaves a driver only
    /// about 30 seconds of real decision time. Anything shorter would be an offer nobody can take.
    /// </summary>
    public const int MinimumMinutes = 1;

    public static DateTime Calculate(
        IConfiguration configuration,
        int availableDriverCount,
        int maxOffersPerBroadcast,
        DeliveryMode mode = DeliveryMode.Regular)
    {
        var manyMinutes = DeliveryModeSettings.Value(configuration, mode, "OfferExpirationManyDriversMinutes", 2);
        var fewMinutes = DeliveryModeSettings.Value(configuration, mode, "OfferExpirationFewDriversMinutes", 5);

        // "Many" = we could fill the whole offer batch; otherwise treat supply as thin.
        var minutes = availableDriverCount >= maxOffersPerBroadcast ? manyMinutes : fewMinutes;
        if (minutes < MinimumMinutes)
            minutes = MinimumMinutes;

        var expiry = DateTime.UtcNow.AddMinutes(minutes);
        return DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
    }
}
