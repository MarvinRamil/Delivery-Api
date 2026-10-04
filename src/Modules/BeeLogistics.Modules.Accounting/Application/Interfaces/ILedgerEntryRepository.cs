using BeeLogistics.Modules.Accounting.Domain;

namespace BeeLogistics.Modules.Accounting.Application.Interfaces;

/// <summary>
/// Read-only queries and append-only writes for ledger. No update/delete.
/// </summary>
public interface ILedgerEntryRepository
{
    Task AddAsync(LedgerEntry entry, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<LedgerEntry> entries, CancellationToken ct = default);
    Task<IReadOnlyList<LedgerEntry>> GetByReferenceAsync(string referenceType, string referenceId, CancellationToken ct = default);
    Task<IReadOnlyList<LedgerEntry>> GetAsync(DateTime? fromUtc = null, DateTime? toUtc = null, string? accountCode = null, int limit = 500, CancellationToken ct = default);
    Task<decimal> GetBalanceAsync(string accountCode, CancellationToken ct = default);
    Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
}
