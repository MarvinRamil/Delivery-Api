using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Application.Interfaces;

public interface IPaymentRepository : IRepository<Domain.Payment>
{
    /// <summary>Looks up by provider checkout id, scoped to the provider so ids can never collide across gateways.</summary>
    Task<Domain.Payment?> GetByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default);
    Task<Domain.Payment?> GetByProviderRefundIdAsync(string provider, string providerRefundId, CancellationToken ct = default);
    /// <summary>
    /// Looks up a payment by the client-supplied idempotency key, scoped to the customer so keys
    /// can never collide across accounts. Backed by a unique index.
    /// </summary>
    Task<Domain.Payment?> GetByCustomerAndIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken ct = default);

    Task<Domain.Payment?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Payment>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default);
    Task<(IReadOnlyList<Domain.Payment> Items, int TotalCount)> GetPagedAsync(int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<Domain.Payment>> GetByBookingIdsAsync(IEnumerable<Guid> bookingIds, CancellationToken ct = default);
    /// <summary>
    /// Find pending payments for a customer with the same amount and no bookingId (for idempotency check).
    /// Returns payments created within the specified time window.
    /// </summary>
    Task<Domain.Payment?> GetPendingPaymentForCustomerAsync(Guid customerId, decimal amount, TimeSpan timeWindow, CancellationToken ct = default);

    /// <summary>
    /// Paid, non-cash payments with no linked booking that were last updated before <paramref name="olderThanUtc"/>
    /// (PayOnline orphans: the customer paid but the booking was never created/linked).
    /// </summary>
    Task<IReadOnlyList<Domain.Payment>> GetOrphanedPaidPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>
    /// Payments stuck in RefundPending whose refund was last touched before <paramref name="olderThanUtc"/>
    /// (the provider's refund webhook never arrived to finalize them).
    /// </summary>
    Task<IReadOnlyList<Domain.Payment>> GetStuckRefundPendingPaymentsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>
    /// Payments still Pending with a live provider checkout, untouched since <paramref name="olderThanUtc"/>
    /// - abandoned checkouts whose links are still payable.
    /// </summary>
    /// <remarks>
    /// "Untouched" is the load-bearing part: any webhook or state change stamps UpdatedAt, so a
    /// payment the customer is actively working through keeps refreshing itself out of this set.
    /// </remarks>
    /// <param name="limit">Ceiling on rows returned, so one run cannot fan out into thousands of provider calls.</param>
    Task<IReadOnlyList<Domain.Payment>> GetAbandonedPendingCheckoutsAsync(DateTime olderThanUtc, int limit, CancellationToken ct = default);
}
