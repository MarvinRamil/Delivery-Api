using BeeLogistics.Modules.Fraud.Domain;

namespace BeeLogistics.Modules.Fraud.Application.Interfaces;

public interface IFraudEventRepository
{
    void Add(FraudEvent evt);
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    Task<int> CountDeliveryCompletedByDriverAsync(Guid driverId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<int> CountOrderCreatedByCustomerAsync(Guid customerId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<int> CountDistinctUsersByDeviceAsync(string deviceId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<FraudEvent?> GetLatestLocationByDriverAsync(Guid driverId, CancellationToken ct = default);

    Task<IReadOnlyList<FraudEvent>> GetEventsAsync(DateTime? from, DateTime? to, FraudEventType? eventType, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountEventsAsync(DateTime? from, DateTime? to, FraudEventType? eventType, CancellationToken ct = default);
}
