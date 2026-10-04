using System.Linq.Expressions;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Fakes;

/// <summary>
/// In-memory IPaymentRepository. Handlers mutate the tracked entities directly,
/// so SaveChangesAsync only counts invocations (useful to assert persistence happened).
/// </summary>
public sealed class FakePaymentRepository : IPaymentRepository
{
    public List<Payment> Payments { get; } = new();
    public int SaveCount { get; private set; }

    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<Payment>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(Payments.ToList());

    public Task<IReadOnlyList<Payment>> FindAsync(Expression<Func<Payment, bool>> predicate, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(Payments.Where(predicate.Compile()).ToList());

    public Task<Payment?> FirstOrDefaultAsync(Expression<Func<Payment, bool>> predicate, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(predicate.Compile()));

    public Task<bool> ExistsAsync(Expression<Func<Payment, bool>> predicate, CancellationToken ct = default)
        => Task.FromResult(Payments.Any(predicate.Compile()));

    public Task<int> CountAsync(Expression<Func<Payment, bool>>? predicate = null, CancellationToken ct = default)
        => Task.FromResult(predicate == null ? Payments.Count : Payments.Count(predicate.Compile()));

    public void Add(Payment entity) => Payments.Add(entity);
    public void Update(Payment entity) { }
    public void Remove(Payment entity) => Payments.Remove(entity);

    /// <summary>
    /// Set to make the next SaveChangesAsync throw DbUpdateConcurrencyException, standing in for a
    /// webhook writing to the same row between a caller's read and its write.
    /// </summary>
    public bool FailNextSaveWithConcurrencyConflict { get; set; }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        if (FailNextSaveWithConcurrencyConflict)
        {
            FailNextSaveWithConcurrencyConflict = false;
            throw new DbUpdateConcurrencyException("Simulated xmin conflict");
        }

        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<Payment?> GetByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.Provider == provider && p.ProviderPaymentId == providerPaymentId));

    public Task<Payment?> GetByProviderRefundIdAsync(string provider, string providerRefundId, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.Provider == provider && p.ProviderRefundId == providerRefundId));

    public Task<Payment?> GetByCustomerAndIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.CustomerId == customerId && p.IdempotencyKey == idempotencyKey));

    public Task<Payment?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.BookingId == bookingId));

    public Task<IReadOnlyList<Payment>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(Payments.Where(p => p.CustomerId == customerId).ToList());

    public Task<(IReadOnlyList<Payment> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<Payment>, int)>((Payments.ToList(), Payments.Count));

    public Task<IReadOnlyList<Payment>> GetByBookingIdsAsync(IEnumerable<Guid> bookingIds, CancellationToken ct = default)
    {
        var ids = bookingIds.ToHashSet();
        return Task.FromResult<IReadOnlyList<Payment>>(
            Payments.Where(p => p.BookingId.HasValue && ids.Contains(p.BookingId.Value)).ToList());
    }

    public Task<Payment?> GetPendingPaymentForCustomerAsync(Guid customerId, decimal amount, TimeSpan timeWindow, CancellationToken ct = default)
        => Task.FromResult(Payments.FirstOrDefault(p => p.CustomerId == customerId && p.Amount == amount && p.BookingId == null));

    public Task<IReadOnlyList<Payment>> GetOrphanedPaidPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(
            Payments.Where(p => p.Status == BeeLogistics.Modules.Payment.Domain.PaymentStatus.Paid
                && p.BookingId == null
                && p.Method != BeeLogistics.Modules.Payment.Domain.PaymentMethod.Cash
                && (p.PaidAt ?? p.CreatedAt) < olderThanUtc).ToList());

    public Task<IReadOnlyList<Payment>> GetStuckRefundPendingPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(
            Payments.Where(p => p.Status == BeeLogistics.Modules.Payment.Domain.PaymentStatus.RefundPending
                && (p.UpdatedAt ?? p.CreatedAt) < olderThanUtc).ToList());

    // Mirrors the SQL query's filters so tests exercise the same selection rules the database applies.
    public Task<IReadOnlyList<Payment>> GetAbandonedPendingCheckoutsAsync(DateTime olderThanUtc, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Payment>>(
            Payments.Where(p => p.Status == BeeLogistics.Modules.Payment.Domain.PaymentStatus.Pending
                    && p.Method != BeeLogistics.Modules.Payment.Domain.PaymentMethod.Cash
                    && p.ProviderPaymentId != null
                    && (p.UpdatedAt ?? p.CreatedAt) < olderThanUtc)
                .OrderBy(p => p.UpdatedAt ?? p.CreatedAt)
                .Take(limit)
                .ToList());
}
