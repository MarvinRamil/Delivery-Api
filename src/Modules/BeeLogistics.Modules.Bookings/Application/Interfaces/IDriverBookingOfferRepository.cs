using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

public interface IDriverBookingOfferRepository : IRepository<DriverBookingOffer>
{
    Task<IReadOnlyList<DriverBookingOffer>> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<DriverBookingOffer?> GetPendingOfferForDriverAsync(Guid driverId, CancellationToken ct = default);
    Task<IReadOnlyList<DriverBookingOffer>> GetPendingOffersForDriverAsync(Guid driverId, int limit, CancellationToken ct = default);
    Task<DriverBookingOffer?> GetNextPendingOfferForBookingAsync(Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<DriverBookingOffer>> GetExpiredOffersAsync(CancellationToken ct = default);
    Task CancelAllPendingOffersForBookingAsync(Guid bookingId, Guid? excludeOfferId = null, CancellationToken ct = default);

    /// <summary>
    /// How each of these drivers has responded to offers since <paramref name="sinceUtc"/>.
    /// </summary>
    /// <remarks>
    /// Feeds the On-Demand responsiveness bias. Like
    /// <see cref="IBookingRepository.GetHeldLegsForDriversAsync"/>, this runs inside candidate
    /// selection on a 15-second job, so it is <b>one grouped query for the whole candidate set</b>
    /// — never one per driver. Drivers with no offers in the window are simply absent from the
    /// result and score neutrally; see <see cref="DriverQualityScore"/>.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, OfferResponseStats>> GetResponseStatsAsync(
        IReadOnlyCollection<Guid> driverIds, DateTime sinceUtc, CancellationToken ct = default);
}
