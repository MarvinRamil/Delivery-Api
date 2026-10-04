using Domain = BeeLogistics.Modules.Rating.Domain;

namespace BeeLogistics.Modules.Rating.Application.Interfaces;

public interface IRatingRepository
{
    Task<Domain.Rating?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Domain.Rating?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Rating>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Rating>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    void Add(Domain.Rating rating);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IDriverRatingRepository
{
    Task<Domain.DriverRating?> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    Task<Domain.DriverRating> GetOrCreateByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    void Add(Domain.DriverRating driverRating);
    void Update(Domain.DriverRating driverRating);
    Task SaveChangesAsync(CancellationToken ct = default);
}
