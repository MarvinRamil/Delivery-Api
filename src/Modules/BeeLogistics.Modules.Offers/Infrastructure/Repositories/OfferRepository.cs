using BeeLogistics.Modules.Offers.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Offers.Infrastructure.Repositories;

public class OfferRepository : IOfferRepository
{
    private readonly OffersDbContext _db;

    public OfferRepository(OffersDbContext db)
    {
        _db = db;
    }

    public Task<Offer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Offers.FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Offer>> GetActiveAsync(OfferAudience? audience, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = _db.Offers
            .Where(o => o.IsActive && o.StartsAt <= now && o.EndsAt >= now);

        if (audience is { } targeted && targeted != OfferAudience.All)
        {
            query = query.Where(o => o.TargetAudience == targeted || o.TargetAudience == OfferAudience.All);
        }

        return await query
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Offer>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Offers.OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    public void Add(Offer offer) => _db.Offers.Add(offer);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
