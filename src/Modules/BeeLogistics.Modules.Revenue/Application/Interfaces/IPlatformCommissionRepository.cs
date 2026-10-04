using BeeLogistics.Modules.Revenue.Domain;

namespace BeeLogistics.Modules.Revenue.Application.Interfaces;

public interface IPlatformCommissionRepository
{
    Task<PlatformCommission?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<PlatformCommission> CreateAsync(PlatformCommission commission, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformCommission>> GetByDriverIdAsync(Guid driverId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformCommission>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
