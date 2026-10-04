using System.Diagnostics.Metrics;

namespace BeeLogistics.Shared.Infrastructure;

/// <summary>
/// Domain metrics for the booking marketplace, exposed via OpenTelemetry
/// (Prometheus /metrics endpoint, configured in Program.cs).
/// Static meter: instruments are process-wide and cheap to call from any module.
/// </summary>
public static class BeeMetrics
{
    public const string MeterName = "BeeLogistics";

    private static readonly Meter Meter = new(MeterName, "1.0");

    // --- Booking dispatch ---
    public static readonly Counter<long> OffersCreated = Meter.CreateCounter<long>(
        "bee.bookings.offers_created", description: "Driver booking offers created");
    public static readonly Counter<long> OffersAccepted = Meter.CreateCounter<long>(
        "bee.bookings.offers_accepted", description: "Offers accepted by drivers");
    /// <summary>
    /// Tag "mode": Regular | OnDemand | Pooling.
    /// </summary>
    /// <remarks>
    /// The mode tag is what makes the pooling economics legible. A pooled booking's discount comes
    /// off the same gross the driver's commission splits, so a driver who takes one and never lands
    /// a second absorbs it — elevated pooled rejects are the expected consequence, and untagged they
    /// would read as a dispatch bug. Compare the rate against Regular before concluding anything is
    /// broken.
    /// </remarks>
    public static readonly Counter<long> OffersRejected = Meter.CreateCounter<long>(
        "bee.bookings.offers_rejected", description: "Offers rejected by drivers");
    /// <summary>Tag "mode": Regular | OnDemand | Pooling. See <see cref="OffersRejected"/>.</summary>
    public static readonly Counter<long> OffersExpired = Meter.CreateCounter<long>(
        "bee.bookings.offers_expired", description: "Offers that expired without a response");
    public static readonly Counter<long> AcceptConflicts = Meter.CreateCounter<long>(
        "bee.bookings.accept_conflicts", description: "Accept attempts that lost the concurrency race");
    public static readonly Counter<long> BookingsRejectedByAll = Meter.CreateCounter<long>(
        "bee.bookings.rejected_by_all", description: "Bookings where every offer was rejected or expired");
    public static readonly Counter<long> OfferBatchConflicts = Meter.CreateCounter<long>(
        "bee.bookings.offer_batch_conflicts", description: "Offer batches dropped due to concurrent creation (unique index)");

    // --- Driver matching ---
    /// <summary>Tag "result": hit | empty.</summary>
    public static readonly Counter<long> MatchingGeoQueries = Meter.CreateCounter<long>(
        "bee.matching.h3_queries", description: "H3 geo-index candidate lookups");
    public static readonly Counter<long> MatchingFallbackScans = Meter.CreateCounter<long>(
        "bee.matching.fallback_scans", description: "Full driver scans because the geo index had no candidates");
    public static readonly Histogram<long> MatchingCandidates = Meter.CreateHistogram<long>(
        "bee.matching.candidates", description: "Eligible drivers found per matching request");

    /// <summary>
    /// Candidates dropped from a booking because their wallet could not cover the platform
    /// commission on it (issue #102).
    /// </summary>
    /// <remarks>
    /// Without this, a booking that reaches nobody because every nearby driver is out of funds is
    /// indistinguishable from one in an area with no drivers — the same empty result, the same
    /// silence. A rising count here means supply is being throttled by wallets, not by geography,
    /// and the fix is drivers topping up rather than anything in dispatch.
    /// </remarks>
    public static readonly Counter<long> CashJobsBlockedByWallet = Meter.CreateCounter<long>(
        "bee.matching.blocked_by_wallet",
        description: "Driver-booking pairs skipped because the driver could not cover the commission");

    /// <summary>
    /// Candidates whose ranking was raised by the pooling detour bias.
    /// </summary>
    /// <remarks>
    /// Answers "is this lever doing anything at all", which is not otherwise observable: the bias
    /// ships weighted at 0, and even once weighted it only fires for drivers already holding work
    /// that happens to pass the pickup. A flat zero after tuning means the weight, the detour
    /// ceiling, or supply — not necessarily a bug.
    /// </remarks>
    public static readonly Counter<long> PoolingBiasApplied = Meter.CreateCounter<long>(
        "bee.matching.pooling_bias_applied", description: "Candidates boosted by the pooling detour score");

    // --- Pricing ---
    /// <summary>
    /// Routes priced from straight-line Haversine because Mapbox was unavailable. Tag "reason":
    /// timeout | no_token | no_route | missing_coordinates | error | http_{status}.
    ///
    /// A fallback and a real answer are indistinguishable in the resulting fare, so without this
    /// counter "our fares look low" is not a diagnosable question.
    /// </summary>
    public static readonly Counter<long> RouteDistanceFallbacks = Meter.CreateCounter<long>(
        "bee.pricing.route_distance_fallbacks", description: "Route distances computed from Haversine instead of Mapbox");

    /// <summary>Route lookups served from cache. A booking is quoted then created, so a healthy
    /// hit rate is roughly half of all lookups; far below that means the cache key is too sharp.</summary>
    public static readonly Counter<long> RouteDistanceCacheHits = Meter.CreateCounter<long>(
        "bee.pricing.route_distance_cache_hits", description: "Route distance lookups served from cache");

    /// <summary>
    /// Server fare minus the client's quote, in pesos. Tags "outcome": accepted | requote.
    ///
    /// This is the number that decides when Pricing:FareTrust:Enforce can be turned on — enable
    /// the 400 only once this histogram is boring.
    /// </summary>
    public static readonly Histogram<double> FareRecomputeDrift = Meter.CreateHistogram<double>(
        "bee.pricing.fare_recompute_drift", unit: "PHP", description: "Server-recomputed fare minus the client's quoted fare");

    /// <summary>
    /// Bookings created, tagged "mode": Regular | OnDemand | Pooling.
    /// </summary>
    /// <remarks>
    /// The adoption signal for delivery modes, and the denominator for every other per-mode
    /// number here. Without it, a per-mode reject rate has nothing to be a rate <i>of</i>.
    /// </remarks>
    public static readonly Counter<long> BookingsCreatedByMode = Meter.CreateCounter<long>(
        "bee.bookings.created_by_mode", description: "Bookings created, by delivery mode");

    // --- Payments ---
    /// <summary>Tag "type": invoice | payout | refund | other.</summary>
    public static readonly Counter<long> WebhooksReceived = Meter.CreateCounter<long>(
        "bee.payments.webhooks_received", description: "Payment provider webhooks received");
    public static readonly Counter<long> WebhooksDuplicate = Meter.CreateCounter<long>(
        "bee.payments.webhooks_duplicate", description: "Duplicate webhook deliveries acknowledged without reprocessing");
    public static readonly Counter<long> WebhooksFailed = Meter.CreateCounter<long>(
        "bee.payments.webhooks_failed", description: "Webhook processing failures (returned 500 for redelivery)");
    /// <summary>Tag "outcome": initiated | pending | succeeded | failed.</summary>
    public static readonly Counter<long> Refunds = Meter.CreateCounter<long>(
        "bee.payments.refunds", description: "Refund lifecycle transitions");
    /// <summary>Paid PayOnline payments left with no linked booking past the grace window (needs ops review).</summary>
    public static readonly Counter<long> PaymentsOrphaned = Meter.CreateCounter<long>(
        "bee.payments.orphaned", description: "Paid payments with no linked booking detected by reconciliation");
    /// <summary>Refunds stuck in RefundPending past the grace window (provider webhook never finalized them).</summary>
    public static readonly Counter<long> RefundsStuck = Meter.CreateCounter<long>(
        "bee.payments.refunds_stuck", description: "Refunds stuck in RefundPending detected by reconciliation");
    /// <summary>Abandoned provider checkouts cancelled by reconciliation so their links stop being payable.</summary>
    public static readonly Counter<long> CheckoutsExpired = Meter.CreateCounter<long>(
        "bee.payments.checkouts_expired", description: "Abandoned provider checkouts expired by reconciliation");
    /// <summary>
    /// Online payments sitting Paid against a Cancelled booking, i.e. money the customer has not got
    /// back. Tag "source": event (counted as the cancellation happened) | sweep (the hourly job's
    /// standing backlog) | refund_failed (automatic refund was attempted and refused).
    ///
    /// This is the number that decides whether automatic refunds are worth turning on, so it ships
    /// before they do.
    /// </summary>
    public static readonly Counter<long> PaymentsCancelledUnrefunded = Meter.CreateCounter<long>(
        "bee.payments.cancelled_unrefunded", description: "Paid payments on cancelled bookings awaiting refund resolution");

    /// <summary>
    /// Provider call latency in milliseconds. Tags: "provider" (paymongo | xendit),
    /// "operation" (e.g. POST /checkout_sessions), "outcome" (success | client_error | server_error | failure).
    ///
    /// Deliberately narrow. AddHttpClientInstrumentation() already emits
    /// http.client.request.duration, which covers per-provider latency via server.address - what it
    /// cannot answer is *which call* is slow, because the URL path is not a tag (it would be
    /// unbounded cardinality). This histogram adds only that missing axis, with the path collapsed
    /// to a bounded operation name.
    /// </summary>
    public static readonly Histogram<double> GatewayRequestDuration = Meter.CreateHistogram<double>(
        "bee.payments.gateway_request_duration", unit: "ms", description: "Payment provider HTTP call duration by operation");

    // --- Drivers outbox ---
    public static readonly Counter<long> OutboxDispatched = Meter.CreateCounter<long>(
        "bee.outbox.dispatched", description: "Outbox messages successfully published");
    public static readonly Counter<long> OutboxFailed = Meter.CreateCounter<long>(
        "bee.outbox.failed", description: "Outbox dispatch attempts that failed");
    public static readonly Counter<long> OutboxPoison = Meter.CreateCounter<long>(
        "bee.outbox.poison", description: "Outbox messages that hit the max-retry cutoff (manual review)");

    /// <summary>
    /// Standing count of undeliverable outbox messages, sampled by the prune job.
    ///
    /// <see cref="OutboxPoison"/> increments once, at the moment a message crosses the cutoff, so
    /// it cannot answer "how many are stuck right now" after a restart. That standing backlog is
    /// what is worth alerting on.
    /// </summary>
    public static readonly Histogram<int> OutboxPoisonBacklog = Meter.CreateHistogram<int>(
        "bee.outbox.poison_backlog", description: "Outbox messages currently past the max-retry cutoff");

    /// <summary>Undelivered messages still within their retry budget. A rising floor means the dispatcher is losing ground.</summary>
    public static readonly Histogram<int> OutboxPendingBacklog = Meter.CreateHistogram<int>(
        "bee.outbox.pending_backlog", description: "Outbox messages awaiting dispatch");

    // --- Driver top-ups ---
    /// <summary>
    /// A driver paid for a top-up and the wallet was not credited. Tag <c>reason</c>:
    /// <c>amount_mismatch</c> | <c>no_wallet</c> | <c>not_found</c> | <c>reconcile_failed</c>.
    ///
    /// Deliberately the same shape as <see cref="PaymentsCancelledUnrefunded"/> so both
    /// directions of "money is not where it belongs" land on one dashboard.
    /// </summary>
    public static readonly Counter<long> DriverTopUpsUncredited = Meter.CreateCounter<long>(
        "bee.driver.topups_uncredited", description: "Paid driver top-ups that were not credited to a wallet");

    /// <summary>
    /// Top-ups the recovery sweep found had been closed as Expired despite being paid. Any
    /// non-zero value means the local expiry timer is writing off real money and the sweep is
    /// the only thing catching it.
    /// </summary>
    public static readonly Counter<long> DriverTopUpsRecoveredAfterExpiry = Meter.CreateCounter<long>(
        "bee.driver.topups_recovered_after_expiry", description: "Expired driver top-ups later found paid and credited");

    /// <summary>A top-up payment the driver attempted and the provider declined. Tag <c>provider</c>.</summary>
    public static readonly Counter<long> DriverTopUpPaymentsFailed = Meter.CreateCounter<long>(
        "bee.driver.topup_payments_failed", description: "Driver top-up payment attempts declined by the provider");
}
