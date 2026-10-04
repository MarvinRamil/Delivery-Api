using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Domain;

public class Payment : Entity
{
    public string PaymentNumber { get; private set; } = null!;
    public Guid? BookingId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "PHP";
    public PaymentStatus Status { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string? ExternalId { get; private set; }

    /// <summary>Gateway that owns this payment ("xendit" | "paymongo"). Refunds/webhooks route by this, never by the active gateway.</summary>
    public string Provider { get; private set; } = "xendit";
    /// <summary>Provider checkout id: Xendit invoice id / PayMongo checkout session id.</summary>
    public string? ProviderPaymentId { get; private set; }
    public string? ProviderCheckoutUrl { get; private set; }
    /// <summary>The id refunds are issued against: Xendit payment_request_id / PayMongo payment id.</summary>
    public string? ProviderCaptureId { get; private set; }
    public string? ProviderRefundId { get; private set; }

    // TODO(provider-cleanup): legacy Xendit-named columns. Frozen (no writers) since the
    // provider-agnostic columns above replaced them; backfilled by AddPaymentProviderColumns.
    // Drop in a follow-up migration once no non-terminal xendit records remain.
    public string? XenditInvoiceId { get; private set; }
    public string? XenditInvoiceUrl { get; private set; }
    public string? XenditPaymentRequestId { get; private set; }
    public string? XenditRefundId { get; private set; }

    public DateTime? PaidAt { get; private set; }
    public DateTime? RefundedAt { get; private set; }
    /// <summary>Driver to debit when the refund is confirmed (captured at refund initiation).</summary>
    public Guid? RefundDriverId { get; private set; }
    /// <summary>Amount of the in-flight/completed refund (supports partial refunds).</summary>
    public decimal? RefundAmount { get; private set; }

    /// <summary>
    /// Cumulative total actually refunded, summed across every refund that reached Succeeded.
    ///
    /// This is the number the cap must be measured against. <see cref="RefundAmount"/> only ever
    /// describes the most recent refund, so capping at <see cref="Amount"/> - as the handler used
    /// to - would let a second partial refund take the total past what was collected.
    ///
    /// Deliberately accumulates only on success. A refund that is accepted by the provider but
    /// later fails has moved no money, so it must not consume refundable headroom.
    /// </summary>
    public decimal TotalRefunded { get; private set; }

    /// <summary>How much of this payment can still be refunded. Never negative.</summary>
    public decimal RefundableAmount => Math.Max(0m, Amount - TotalRefunded);

    public string? FailureReason { get; private set; }

    private Payment() { }

    public static Payment Create(Guid? bookingId, Guid customerId, decimal amount, PaymentMethod method, string currency = "PHP")
    {
        return new Payment
        {
            BookingId = bookingId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Method = method,
            Status = PaymentStatus.Pending,
            PaymentNumber = $"PAY-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..8].ToUpper()}"
        };
    }
    
    /// <summary>
    /// Link this payment to a booking after booking is created (for PayOnline flow where payment is created first).
    /// </summary>
    public void LinkToBooking(Guid bookingId)
    {
        if (BookingId.HasValue && BookingId.Value != bookingId)
            throw new InvalidOperationException("Payment is already linked to a different booking");
        
        BookingId = bookingId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Creates a cash-on-delivery payment (already marked Paid so earning/settlement consumers run when booking completes).</summary>
    public static Payment CreateCashOnDelivery(Guid bookingId, Guid customerId, decimal amount, string currency = "PHP")
    {
        var payment = Create(bookingId, customerId, amount, PaymentMethod.Cash, currency);
        payment.MarkAsPaid(DateTime.UtcNow);
        return payment;
    }

    public void SetProviderCheckout(string provider, string providerPaymentId, string checkoutUrl, string externalId)
    {
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        ProviderCheckoutUrl = checkoutUrl;
        ExternalId = externalId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Optional client-supplied key that makes creation idempotent, so a double-submit returns
    /// the first payment instead of opening a second real checkout at the provider. Scoped per
    /// customer by a unique index, matching DriverTopUp and WithdrawalRequest.
    /// </summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>
    /// PostgreSQL xmin, mapped as a concurrency token. Without it a webhook settlement racing a
    /// refund was last-write-wins - which mattered most for PayMongo refunds, since those carry
    /// no provider idempotency key and rely entirely on the RefundPending status guard holding
    /// on this row.
    /// </summary>
    public uint Version { get; private set; }

    public void SetIdempotencyKey(string? idempotencyKey)
        => IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();

    public void SetProviderCaptureId(string captureId)
    {
        ProviderCaptureId = captureId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records a refund that the provider has confirmed, adding it to <see cref="TotalRefunded"/>.
    /// </summary>
    /// <param name="amountRefunded">
    /// What actually moved. Defaults to the in-flight <see cref="RefundAmount"/>, falling back to
    /// the full <see cref="Amount"/> for the synchronous full-refund path that never went Pending.
    /// </param>
    /// <remarks>
    /// Idempotent by design, and that is load-bearing rather than cosmetic: the refund handler
    /// re-applies this action after an xmin concurrency conflict, and refund webhooks are
    /// redelivered. Without the guard either route would accumulate the same money twice and
    /// silently shrink the refundable balance.
    /// </remarks>
    public void MarkAsRefunded(DateTime? refundedAt = null, string? providerRefundId = null, decimal? amountRefunded = null)
    {
        if (Status == PaymentStatus.Refunded)
            return;

        var applied = amountRefunded ?? RefundAmount ?? Amount;

        // Clamp as a last line of defence so the stored total can never claim more was returned
        // than was ever collected. The real gate is the cap check in RefundPaymentCommandHandler;
        // reaching this clamp means something upstream let a bad amount through.
        applied = Math.Clamp(applied, 0m, RefundableAmount);

        TotalRefunded += applied;
        RefundAmount = applied;
        Status = PaymentStatus.Refunded;
        RefundedAt = refundedAt ?? DateTime.UtcNow;
        ProviderRefundId = providerRefundId ?? ProviderRefundId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A refund was accepted by the provider but not yet confirmed. Provider refunds
    /// are asynchronous: the final outcome arrives via a refund webhook.
    /// </summary>
    public void MarkAsRefundPending(string providerRefundId, Guid refundDriverId, decimal refundAmount)
    {
        Status = PaymentStatus.RefundPending;
        ProviderRefundId = providerRefundId;
        RefundDriverId = refundDriverId;
        RefundAmount = refundAmount;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reserves the payment for an in-flight refund before the provider has been called yet, so
    /// the provider id isn't known. Guarded by <see cref="Version"/> (xmin): the caller saves this
    /// change immediately, and a second concurrent refund attempt that read the row before that
    /// save commits will conflict on its own save instead of both reaching the gateway. Call the
    /// three-argument overload once the provider responds, to record the real id without
    /// re-entering RefundPending from scratch - the unconditional overwrite makes calling this
    /// twice in a row safe.
    /// </summary>
    public void MarkAsRefundPending(Guid refundDriverId, decimal refundAmount)
    {
        Status = PaymentStatus.RefundPending;
        ProviderRefundId = null;
        RefundDriverId = refundDriverId;
        RefundAmount = refundAmount;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// The provider reported the refund failed. The payment itself is still paid,
    /// so the status returns to Paid with the failure recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RefundAmount"/> and <see cref="RefundDriverId"/> are deliberately <b>kept</b>.
    /// They describe the refund this failure belongs to, and a later <c>SUCCEEDED</c> webhook for
    /// the same provider refund id still needs both: who to debit, and for how much. Provider
    /// webhooks are redelivered and are not ordered, so a <c>FAILED</c> landing before a
    /// <c>SUCCEEDED</c> is a delivery artefact rather than a genuine state flip - and the
    /// <c>SUCCEEDED</c> branch is not short-circuited here, because after a failure the status is
    /// <c>Paid</c>, not <c>Refunded</c>.
    /// </para>
    /// <para>
    /// #34 briefly cleared both, reasoning that a stale amount could leak into a later refund. It
    /// cannot: starting another refund calls <see cref="MarkAsRefundPending"/>, which overwrites
    /// <see cref="RefundAmount"/>, <see cref="RefundDriverId"/> and <see cref="ProviderRefundId"/>
    /// together - and the webhook handler resolves the payment *by* <see cref="ProviderRefundId"/>,
    /// so a stale webhook would no longer find this row at all. Clearing bought nothing and left
    /// the driver holding money that had been returned to the customer (#38).
    /// </para>
    /// <para>
    /// <see cref="TotalRefunded"/> is untouched either way, because a failed refund moved no money.
    /// </para>
    /// </remarks>
    public void MarkRefundFailed(string reason)
    {
        Status = PaymentStatus.Paid;
        FailureReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsPaid(DateTime paidAt)
    {
        Status = PaymentStatus.Paid;
        PaidAt = paidAt;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// True once money has actually moved for this payment. Late or out-of-order provider
    /// events must never drag the record back out of one of these states.
    /// </summary>
    private bool IsInTerminalMoneyState =>
        Status is PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.RefundPending;

    /// <summary>
    /// No-op once the payment has reached a terminal money state. PayMongo maps every non-paid
    /// checkout event to EXPIRED, so a single late delivery would otherwise flip a paid payment
    /// to Failed/Expired — and EarningCreditConsumer gates the driver's earnings on Status == Paid.
    /// Guarded here rather than at the call site so every caller inherits the protection.
    /// </summary>
    public void MarkAsFailed(string reason)
    {
        if (IsInTerminalMoneyState)
            return;

        Status = PaymentStatus.Failed;
        FailureReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <inheritdoc cref="MarkAsFailed(string)"/>
    public void MarkAsExpired()
    {
        if (IsInTerminalMoneyState)
            return;

        Status = PaymentStatus.Expired;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum PaymentStatus
{
    Pending,
    Paid,
    Failed,
    Expired,
    Refunded,
    // Stored as int: only append values, never reorder.
    RefundPending
}

public enum PaymentMethod
{
    BankTransfer,
    EWallet,
    CreditCard,
    QrCode,
    Cash
}
