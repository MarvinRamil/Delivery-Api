using BeeLogistics.Modules.Accounting.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Accounting.Infrastructure;

/// <inheritdoc cref="IAccountingUnitOfWork"/>
public sealed class AccountingUnitOfWork : IAccountingUnitOfWork
{
    private readonly AccountingDbContext _context;

    public AccountingUnitOfWork(AccountingDbContext context) => _context = context;

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        // Nested call (or an ambient transaction from a caller): just run the work so it joins
        // the existing boundary rather than opening a second one.
        if (_context.Database.CurrentTransaction is not null)
        {
            await work(ct);
            return;
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // The repositories' own SaveChanges calls enlist in this transaction automatically, so
        // nothing is visible to other readers until the commit below.
        await work(ct);

        await transaction.CommitAsync(ct);
    }
}
