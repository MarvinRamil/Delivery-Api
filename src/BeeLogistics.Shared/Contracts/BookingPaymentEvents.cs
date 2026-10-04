namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Payload sent to customers when a booking payment is paid (webhook processed).
/// Used by SSE stream and SignalR so the app can update payment status immediately.
/// BookingId may be null/Empty for PayOnline flow where payment is created before booking.
/// </summary>
public sealed record BookingPaymentPaidPayload(
    Guid PaymentId,
    Guid BookingId,  // Empty Guid if booking not created yet (for PayOnline flow)
    Guid CustomerId,
    decimal Amount,
    string Status,
    DateTime PaidAtUtc
);

/// <summary>
/// Broadcasts real-time booking payment events to connected customers (SSE and/or SignalR).
/// Implemented in the API host so the Payment module stays decoupled from transport.
/// </summary>
public interface IBookingPaymentEventBroadcaster
{
    /// <summary>
    /// Notify that a customer's booking payment was paid.
    /// </summary>
    Task PublishPaidAsync(BookingPaymentPaidPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to paid events for a customer (for SSE). Returns a stream that completes when the subscription is disposed or the token is cancelled.
    /// </summary>
    IAsyncEnumerable<BookingPaymentPaidPayload> SubscribeAsync(Guid customerId, CancellationToken ct = default);
}
