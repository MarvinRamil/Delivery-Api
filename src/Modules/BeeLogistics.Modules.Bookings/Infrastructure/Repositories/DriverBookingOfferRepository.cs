using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

public class DriverBookingOfferRepository : Repository<DriverBookingOffer, BookingsDbContext>, IDriverBookingOfferRepository
{
    public DriverBookingOfferRepository(BookingsDbContext context) : base(context) { }

    public async Task<IReadOnlyList<DriverBookingOffer>> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(o => o.BookingId == bookingId)
            .OrderBy(o => o.SequenceNumber)
            .ToListAsync(ct);
    }

    public async Task<DriverBookingOffer?> GetPendingOfferForDriverAsync(Guid driverId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(o => o.DriverId == driverId && o.Status == DriverOfferStatus.Pending)
            .OrderBy(o => o.OfferedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The driver's inbox: live offers, highest dispatch rank first, then oldest.
    /// </summary>
    /// <remarks>
    /// Rank has to come from the booking, and <see cref="DriverBookingOffer"/> has no navigation to
    /// one — <c>BookingsDbContext</c> maps the relationship as <c>HasOne&lt;Booking&gt;().WithMany()</c>
    /// with no property — hence the explicit join. Denormalising DispatchPriority onto the offer row
    /// was the alternative and was rejected: a second copy of derived data that can drift, to save a
    /// join on an endpoint that returns at most ten rows behind IX_DriverBookingOffers_DriverId_Status.
    ///
    /// The join is an inner join, so an offer whose booking is gone drops out here rather than being
    /// returned and skipped by the caller. Same result, one less round trip; the FK makes it moot.
    ///
    /// This ordering can starve an older low-rank offer for a driver polling with a small limit
    /// while higher-rank offers keep arriving. Bounded by the offer TTL, and visible in
    /// <c>offers_expired</c>.
    /// </remarks>
    public async Task<IReadOnlyList<DriverBookingOffer>> GetPendingOffersForDriverAsync(Guid driverId, int limit, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await DbSet
            .Where(o => o.DriverId == driverId
                && o.Status == DriverOfferStatus.Pending
                && o.ExpiresAt > now) // Only non-expired offers
            .Join(
                Context.Bookings,
                o => o.BookingId,
                b => b.Id,
                (o, b) => new { Offer = o, b.DispatchPriority })
            .OrderByDescending(x => x.DispatchPriority)
            .ThenBy(x => x.Offer.OfferedAt) // Oldest first within a rank
            .Take(limit)
            .Select(x => x.Offer)
            .ToListAsync(ct);
    }

    public async Task<DriverBookingOffer?> GetNextPendingOfferForBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await DbSet
            .Where(o => o.BookingId == bookingId && o.Status == DriverOfferStatus.Pending)
            .OrderBy(o => o.SequenceNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DriverBookingOffer>> GetExpiredOffersAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Grace period: do not consider an offer expired until it has been offered at least 30 seconds ago.
        // Prevents freshly created offers from being marked Expired by the job (race/clock skew).
        var offeredBefore = now.AddSeconds(-30);
        return await DbSet
            .Where(o => o.Status == DriverOfferStatus.Pending && o.ExpiresAt < now && o.OfferedAt < offeredBefore)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, OfferResponseStats>> GetResponseStatsAsync(
        IReadOnlyCollection<Guid> driverIds, DateTime sinceUtc, CancellationToken ct = default)
    {
        if (driverIds.Count == 0)
            return new Dictionary<Guid, OfferResponseStats>();

        var ids = driverIds.ToList();

        // One grouped query, counted in the database. Offers still Pending are excluded from both
        // counts: a driver cannot have declined an offer they are still looking at, and counting
        // them as unanswered would penalise whoever was asked most recently.
        var rows = await DbSet
            .Where(o => ids.Contains(o.DriverId)
                && o.OfferedAt >= sinceUtc
                && o.Status != DriverOfferStatus.Pending)
            .GroupBy(o => o.DriverId)
            .Select(g => new
            {
                DriverId = g.Key,
                Offered = g.Count(),
                Accepted = g.Count(o => o.Status == DriverOfferStatus.Accepted),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.DriverId, r => new OfferResponseStats(r.Offered, r.Accepted));
    }

    public async Task CancelAllPendingOffersForBookingAsync(Guid bookingId, Guid? excludeOfferId = null, CancellationToken ct = default)
    {
        var query = DbSet
            .Where(o => o.BookingId == bookingId && o.Status == DriverOfferStatus.Pending);
        
        // Exclude the offer that was just accepted (if provided)
        if (excludeOfferId.HasValue)
        {
            query = query.Where(o => o.Id != excludeOfferId.Value);
        }

        var offers = await query.ToListAsync(ct);

        foreach (var offer in offers)
        {
            // Safety check: Only expire if still pending (handles race conditions)
            if (offer.Status == DriverOfferStatus.Pending)
            {
                offer.Expire();
            }
        }
    }
}
