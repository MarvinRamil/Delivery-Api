namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Published (via IBus, bypassing the outbox) when a payment provider checkout
/// webhook arrives, for immediate SSE delivery and driver top-up crediting.
/// </summary>
public sealed record PaymentCheckoutWebhookReceived
{
    /// <summary>Canonical provider name: "xendit" | "paymongo".</summary>
    public required string Provider { get; init; }
    /// <summary>Provider checkout id: Xendit invoice id / PayMongo checkout session id.</summary>
    public required string ProviderPaymentId { get; init; }
    public required string Status { get; init; }
    public DateTime? PaidAt { get; init; }
    public string? ExternalId { get; init; }
    public decimal? PaidAmount { get; init; }
}

// PaymentSettledEvent was removed here: it was never published by anything, because cashless
// money is deliberately held until the delivery completes. The driver is credited by
// EarningCreditConsumer on BookingCompletedEvent, not on payment settlement. The consumer that
// listened for it was bound to a queue that never received a message, and read as a live
// crediting path to anyone tracing the code.

/// <summary>
/// Published when a cashless payment is refunded. Driver wallet consumer only debits (EarningReversal)
/// if the driver was previously credited for this booking (i.e. delivery had completed).
/// </summary>
public sealed record PaymentRefundedEvent
{
    public required Guid BookingId { get; init; }
    public required Guid DriverId { get; init; }
    public required decimal AmountRefunded { get; init; }
}

/// <summary>
/// Published when a booking (delivery) payment is marked Paid. Notification/Bookings consumes to send payment receipt email to customer.
/// </summary>
public sealed record BookingPaymentReceiptRequestedEvent
{
    public required Guid CustomerId { get; init; }
    public required string PaymentNumber { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? CheckoutUrl { get; init; }
    public required DateTime PaidAt { get; init; }
}
