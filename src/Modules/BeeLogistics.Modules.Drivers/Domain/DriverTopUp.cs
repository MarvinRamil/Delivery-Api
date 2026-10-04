using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public class DriverTopUp : Entity
{
    public Guid DriverId { get; private set; }
    public Guid WalletId { get; private set; }
    public decimal Amount { get; private set; }
    public DriverTopUpStatus Status { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public string? IdempotencyKey { get; private set; }

    /// <summary>Gateway that owns this top-up ("xendit" | "paymongo"). Reconciliation/webhooks route by this.</summary>
    public string Provider { get; private set; } = "xendit";
    /// <summary>Provider checkout id: Xendit invoice id / PayMongo checkout session id.</summary>
    public string? ProviderPaymentId { get; private set; }
    public string? ProviderCheckoutUrl { get; private set; }

    // TODO(provider-cleanup): legacy Xendit-named columns. Frozen (no writers) since the
    // provider-agnostic columns above replaced them; backfilled by AddDriverProviderColumns.
    public string? XenditInvoiceId { get; private set; }
    public string? XenditInvoiceUrl { get; private set; }

    public DateTime? ExpiresAt { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime? CreditedAt { get; private set; }
    public DateTime? ReconciledAt { get; private set; }
    public uint Version { get; private set; } // Postgres xmin mapping

    private DriverTopUp()
    {
    }

    public DriverTopUp(Guid driverId, Guid walletId, decimal amount, string externalId, string? idempotencyKey = null)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("ExternalId is required", nameof(externalId));

        DriverId = driverId;
        WalletId = walletId;
        Amount = amount;
        ExternalId = externalId;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        Status = DriverTopUpStatus.Pending;
        CreatedAt = DateTime.UtcNow;
    }

    public void SetProviderCheckout(string provider, string providerPaymentId, string checkoutUrl, DateTime? expiresAt)
    {
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        ProviderCheckoutUrl = checkoutUrl;
        ExpiresAt = expiresAt;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsPaid(DateTime paidAtUtc)
    {
        Status = DriverTopUpStatus.Paid;
        PaidAt = paidAtUtc;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// True once the money has landed: either the record says Paid, or a credit was applied.
    /// Both are checked because they are set by different steps and a webhook can arrive
    /// between them.
    /// </summary>
    public bool IsSettled => Status == DriverTopUpStatus.Paid || CreditedAt.HasValue;

    /// <summary>
    /// Closes an unpaid top-up. No-ops on a settled record.
    /// </summary>
    /// <remarks>
    /// The guard is not defensive padding. The expiry sweep and a late provider webhook can both
    /// reach a top-up that has already been paid and credited - most easily when a driver's first
    /// attempt is declined and their retry succeeds - and without it the record would flip to a
    /// terminal state while the wallet keeps the money, leaving the ledger contradicting itself.
    /// </remarks>
    public void MarkAsExpired()
    {
        if (IsSettled)
            return;

        Status = DriverTopUpStatus.Expired;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// User or admin cancelled the top-up before payment (only valid when Status is Pending).
    /// </summary>
    public void MarkAsCancelled(string? reason = null)
    {
        if (Status != DriverTopUpStatus.Pending)
            throw new InvalidOperationException("Only pending top-ups can be cancelled.");
        Status = DriverTopUpStatus.Cancelled;
        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by user" : reason.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Terminally fails an unpaid top-up. No-ops on a settled record, for the same reason as
    /// <see cref="MarkAsExpired"/>.
    /// </summary>
    public void MarkAsFailed(string reason)
    {
        if (IsSettled)
            return;

        Status = DriverTopUpStatus.Failed;
        FailureReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records that the driver attempted payment and the provider declined it, **without**
    /// changing status.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="MarkAsFailed"/>. A declined attempt does not necessarily kill
    /// the provider's checkout session - the driver may simply pick another method and succeed on
    /// the same link - so closing the record here would break their retry. Whether a decline is
    /// terminal is decided by the session's own status, which arrives as a separate event or is
    /// read by reconciliation.
    /// </remarks>
    public void RecordFailedAttempt(string reason)
    {
        if (IsSettled)
            return;

        FailureReason = string.IsNullOrWhiteSpace(reason) ? "Payment attempt declined" : reason.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkCredited()
    {
        CreditedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkReconciled()
    {
        ReconciledAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum DriverTopUpStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Expired = 3,
    Cancelled = 4
}
