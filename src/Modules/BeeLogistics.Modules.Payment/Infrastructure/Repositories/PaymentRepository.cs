using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Payment.Infrastructure.Repositories;

public class PaymentRepository : Repository<Domain.Payment, PaymentDbContext>, IPaymentRepository
{
    public PaymentRepository(PaymentDbContext context) : base(context) { }

    public async Task<Domain.Payment?> GetByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderPaymentId == providerPaymentId, ct);

    public async Task<Domain.Payment?> GetByProviderRefundIdAsync(string provider, string providerRefundId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderRefundId == providerRefundId, ct);

    public async Task<Domain.Payment?> GetByCustomerAndIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(p => p.CustomerId == customerId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task<Domain.Payment?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
        => await DbSet.FirstOrDefaultAsync(p => p.BookingId.HasValue && p.BookingId.Value == bookingId, ct);

    public async Task<IReadOnlyList<Domain.Payment>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
        => await DbSet.Where(p => p.CustomerId == customerId).ToListAsync(ct);

    public async Task<(IReadOnlyList<Domain.Payment> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = DbSet.AsQueryable();
        var totalCount = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, totalCount);
    }

    public async Task<IReadOnlyList<Domain.Payment>> GetByBookingIdsAsync(IEnumerable<Guid> bookingIds, CancellationToken ct = default)
    {
        var idList = bookingIds.Distinct().ToList();
        if (idList.Count == 0)
            return Array.Empty<Domain.Payment>();
        // Filter payments where BookingId is not null and matches one of the provided booking IDs
        return await DbSet
            .Where(p => p.BookingId != null)
            .Where(p => idList.Contains(p.BookingId!.Value))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Domain.Payment>> GetOrphanedPaidPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => await DbSet
            .Where(p => p.Status == PaymentStatus.Paid)
            .Where(p => p.BookingId == null)
            .Where(p => p.Method != PaymentMethod.Cash)
            .Where(p => (p.PaidAt ?? p.CreatedAt) < olderThanUtc)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Domain.Payment>> GetStuckRefundPendingPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default)
        => await DbSet
            .Where(p => p.Status == PaymentStatus.RefundPending)
            .Where(p => (p.UpdatedAt ?? p.CreatedAt) < olderThanUtc)
            .OrderBy(p => p.UpdatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Domain.Payment>> GetAbandonedPendingCheckoutsAsync(DateTime olderThanUtc, int limit, CancellationToken ct = default)
        => await DbSet
            .Where(p => p.Status == PaymentStatus.Pending)
            // Cash has no provider checkout to expire, and no ProviderPaymentId means the gateway
            // call never completed - there is nothing at the provider to cancel.
            .Where(p => p.Method != PaymentMethod.Cash)
            .Where(p => p.ProviderPaymentId != null)
            .Where(p => (p.UpdatedAt ?? p.CreatedAt) < olderThanUtc)
            // Oldest first: the most certainly dead links go before the per-run ceiling bites.
            .OrderBy(p => p.UpdatedAt ?? p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<Domain.Payment?> GetPendingPaymentForCustomerAsync(Guid customerId, decimal amount, TimeSpan timeWindow, CancellationToken ct = default)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(timeWindow);
        return await DbSet
            .Where(p => p.CustomerId == customerId)
            .Where(p => p.Amount == amount)
            .Where(p => p.BookingId == null) // No booking linked yet (pre-booking payment)
            .Where(p => p.Status == PaymentStatus.Pending)
            .Where(p => p.CreatedAt >= cutoffTime) // Created within time window
            .OrderByDescending(p => p.CreatedAt) // Get most recent
            .FirstOrDefaultAsync(ct);
    }
}
