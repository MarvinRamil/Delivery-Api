using BeeLogistics.Modules.Accounting.Domain;

namespace BeeLogistics.Modules.Accounting.Application.Interfaces;

public interface ISalesEntryRepository
{
    Task AddAsync(SalesEntry entry, CancellationToken ct = default);
    Task<SalesEntry?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<SalesEntry>> GetAsync(DateTime? fromUtc = null, DateTime? toUtc = null, string? paymentMethod = null, int limit = 500, CancellationToken ct = default);
}
