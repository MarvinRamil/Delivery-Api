using BeeLogistics.Modules.Fraud.Domain;

namespace BeeLogistics.Modules.Fraud.Application.Interfaces;

public interface IFraudSignalRepository
{
    void Add(FraudSignal signal);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FraudSignal>> GetSignalsAsync(DateTime? from, DateTime? to, string? ruleName, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountSignalsAsync(DateTime? from, DateTime? to, string? ruleName, CancellationToken ct = default);
}
