using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Shared.Infrastructure;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Surfaces payments that the webhook-driven flow can leave stranded so they don't fail silently:
/// <list type="bullet">
/// <item>PayOnline orphans — a payment marked Paid that never got a booking linked (app died after pay, before create).</item>
/// <item>Stuck refunds — payments in RefundPending whose provider refund webhook never arrived to finalize them.</item>
/// <item>Abandoned checkouts — Pending payments whose provider checkout link is still payable.</item>
/// <item>Cancelled-but-paid — bookings that reached Cancelled with an online payment still Paid,
/// i.e. money the customer has not got back (GitLab #51).</item>
/// </list>
/// PayOnline orphans and cancelled-but-paid bookings are detection only: metrics and alert logs, no
/// mutation (whether a cancelled-but-paid booking is refunded automatically is decided on the live
/// path, not here).
///
/// Abandoned checkouts and stuck refunds <b>do</b> mutate, at the provider as well as locally.
/// Abandoned-checkout expiry was a deliberate change (GitLab #36) rather than a drift: an abandoned
/// PayMongo checkout stays payable until PayMongo expires it on its own schedule, so a customer can
/// pay against a stale link for a booking that has moved on — producing exactly the orphan this same
/// job detects but cannot repair. It is gated behind
/// <see cref="PaymentReconciliationOptions.ExpireAbandonedCheckouts"/> so it can be switched off
/// without a deploy. Stuck-refund resolution (<see cref="IStuckRefundResolver"/>) asks the provider
/// directly what happened to a refund whose webhook never arrived, rather than leaving it to wait
/// indefinitely.
///
/// Wired as an hourly Hangfire job.
/// </summary>
public class PaymentReconciliationService
{
    // Give the webhook + client time to complete the normal flow before flagging.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StuckRefundGrace = TimeSpan.FromHours(6);

    private readonly IPaymentRepository _paymentRepository;
    private readonly IAbandonedCheckoutExpirer _checkoutExpirer;
    private readonly ICancelledBookingPaymentAuditor _cancelledBookingAuditor;
    private readonly IStuckRefundResolver _stuckRefundResolver;
    private readonly ILogger<PaymentReconciliationService> _logger;

    public PaymentReconciliationService(
        IPaymentRepository paymentRepository,
        IAbandonedCheckoutExpirer checkoutExpirer,
        ICancelledBookingPaymentAuditor cancelledBookingAuditor,
        IStuckRefundResolver stuckRefundResolver,
        ILogger<PaymentReconciliationService> logger)
    {
        _paymentRepository = paymentRepository;
        _checkoutExpirer = checkoutExpirer;
        _cancelledBookingAuditor = cancelledBookingAuditor;
        _stuckRefundResolver = stuckRefundResolver;
        _logger = logger;
    }

    // Hangfire's recurring-job scheduler takes a distributed lock so only one instance *enqueues*
    // each occurrence, but nothing stopped two runs overlapping if one ran long. The job is
    // read-only so an overlap moves no money - it double-counts PaymentsOrphaned/RefundsStuck,
    // which are exactly the counters worth alerting on. Matches the booking jobs, which already
    // carry this attribute.
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 1)]
    public async Task ReconcileAsync()
    {
        var now = DateTime.UtcNow;

        var orphans = await _paymentRepository.GetOrphanedPaidPaymentsAsync(now - OrphanGrace);
        if (orphans.Count > 0)
        {
            BeeMetrics.PaymentsOrphaned.Add(orphans.Count);
            _logger.LogWarning(
                "[PaymentReconciliation] {Count} paid payment(s) have no linked booking after {Minutes} min (customer paid, booking not created/linked). Payment IDs: {PaymentIds}",
                orphans.Count, OrphanGrace.TotalMinutes,
                string.Join(", ", orphans.Take(20).Select(p => p.Id)));
        }

        var stuckRefunds = await _paymentRepository.GetStuckRefundPendingPaymentsAsync(now - StuckRefundGrace);
        var stuckRefundsResolved = 0;
        if (stuckRefunds.Count > 0)
        {
            // Report the backlog before attempting to clear it, so the metric reflects what was
            // actually found stuck this run, not what's left over after resolution.
            BeeMetrics.RefundsStuck.Add(stuckRefunds.Count);
            _logger.LogWarning(
                "[PaymentReconciliation] {Count} refund(s) stuck in RefundPending for over {Hours}h (provider refund webhook never finalized). Payment IDs: {PaymentIds}",
                stuckRefunds.Count, StuckRefundGrace.TotalHours,
                string.Join(", ", stuckRefunds.Take(20).Select(p => p.Id)));

            stuckRefundsResolved = await _stuckRefundResolver.ResolveAsync(now - StuckRefundGrace);
        }

        // Detection only, and a backstop for BookingCancelledPaymentConsumer rather than the live
        // path - see ICancelledBookingPaymentAuditor. Module-side for the same testability reason
        // as the expirer below.
        var cancelledUnrefunded = await _cancelledBookingAuditor.AuditAsync(now);

        // The only mutating step. Its logic lives in the Payment module rather than here so it is
        // reachable by tests - this project is not referenced by the test assembly.
        var expired = await _checkoutExpirer.ExpireAsync(now);

        if (orphans.Count == 0 && stuckRefunds.Count == 0 && stuckRefundsResolved == 0 && cancelledUnrefunded == 0 && expired == 0)
            _logger.LogDebug("[PaymentReconciliation] Nothing to reconcile.");
    }
}
