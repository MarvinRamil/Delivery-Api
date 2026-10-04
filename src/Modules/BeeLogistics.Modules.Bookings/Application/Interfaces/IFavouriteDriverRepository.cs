using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

public interface IFavouriteDriverRepository
{
    Task<FavouriteDriver?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<FavouriteDriver?> GetByCustomerAndDriverAsync(Guid customerId, Guid driverId, CancellationToken ct = default);
    Task<IReadOnlyList<FavouriteDriver>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<FavouriteDriver>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    void Add(FavouriteDriver favouriteDriver);
    void Remove(FavouriteDriver favouriteDriver);
    Task SaveChangesAsync(CancellationToken ct = default);
}
