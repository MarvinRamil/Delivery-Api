using BeeLogistics.Modules.Offers.Domain;

namespace BeeLogistics.Modules.Offers.Infrastructure;

public interface IOfferRepository
{
    Task<Offer?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Offer>> GetActiveAsync(OfferAudience? audience, CancellationToken ct = default);
    Task<IReadOnlyList<Offer>> GetAllAsync(CancellationToken ct = default);
    void Add(Offer offer);
    Task SaveChangesAsync(CancellationToken ct = default);
}
