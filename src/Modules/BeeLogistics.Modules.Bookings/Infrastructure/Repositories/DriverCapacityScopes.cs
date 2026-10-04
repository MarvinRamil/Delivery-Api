using BeeLogistics.Modules.Bookings.Application.Interfaces;
using Microsoft.EntityFrameworkCore.Storage;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

/// <summary>
/// Postgres implementation: a transaction holding a per-driver advisory lock. The lock is
/// transaction-scoped, so it is released by the commit or the rollback either way — nothing to
/// leak if the handler throws.
/// </summary>
internal sealed class PostgresDriverCapacityScope : IDriverCapacityScope
{
    private readonly IDbContextTransaction _transaction;

    public PostgresDriverCapacityScope(IDbContextTransaction transaction) => _transaction = transaction;

    public Task CommitAsync(CancellationToken ct = default) => _transaction.CommitAsync(ct);

    // Disposing an uncommitted EF transaction rolls it back, which is exactly the behaviour we
    // want for a rejected or failed accept.
    public ValueTask DisposeAsync() => _transaction.DisposeAsync();
}

/// <summary>
/// Used when the provider has no transactions to lock inside — the in-memory provider in tests,
/// and any future non-Postgres provider.
/// </summary>
/// <remarks>
/// The cap itself is still enforced; only the guard against a driver's two simultaneous accepts
/// is absent, which cannot happen in a single-threaded test anyway.
/// </remarks>
internal sealed class NoOpDriverCapacityScope : IDriverCapacityScope
{
    public static readonly NoOpDriverCapacityScope Instance = new();

    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
