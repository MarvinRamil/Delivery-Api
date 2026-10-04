namespace BeeLogistics.Modules.Accounting.Application.Interfaces;

/// <summary>
/// Runs several accounting writes as one atomic unit.
///
/// The repositories each call SaveChanges internally, so recording a sale used to commit the
/// ledger entries and the SalesEntry separately. A failure in between left the ledger written and
/// the sales row missing - and because the retry short-circuited on the ledger's idempotency key,
/// the sales row was never written at all. The two tables then disagreed permanently.
///
/// Lives in Application so consumers can control the boundary without depending on
/// AccountingDbContext directly.
/// </summary>
public interface IAccountingUnitOfWork
{
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}
