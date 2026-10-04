using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Payment.Application.Consumers;

/// <summary>
/// When a provider checkout webhook reports the payment is paid for a booking payment,
/// broadcast the event via SSE/SignalR so the customer app can update immediately without polling.
/// </summary>
public class BookingPaymentWebhookConsumer : IConsumer<PaymentCheckoutWebhookReceived>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IBookingPaymentEventBroadcaster? _eventBroadcaster;
    private readonly ILogger<BookingPaymentWebhookConsumer> _logger;

    public BookingPaymentWebhookConsumer(
        IPaymentRepository paymentRepository,
        IBookingPaymentEventBroadcaster? eventBroadcaster = null,
        ILogger<BookingPaymentWebhookConsumer> logger = null!)
    {
        _paymentRepository = paymentRepository;
        _eventBroadcaster = eventBroadcaster;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentCheckoutWebhookReceived> context)
    {
        var consumeStartTime = DateTime.UtcNow;
        var msg = context.Message;

        // ALWAYS log consumer invocation - critical for debugging
        _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] ===== CONSUMER INVOKED ===== Consuming PaymentCheckoutWebhookReceived - Provider: {Provider}, ProviderPaymentId: {ProviderPaymentId}, Status: {Status}, PaidAt: {PaidAt}",
            consumeStartTime, msg.Provider, msg.ProviderPaymentId, msg.Status, msg.PaidAt?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? "null");

        var status = msg.Status?.ToUpperInvariant();
        if (status != "PAID" && status != "SETTLED")
        {
            _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Skipping webhook - Status {Status} is not PAID/SETTLED",
                DateTime.UtcNow, status);
            return;
        }

        var payment = await _paymentRepository.GetByProviderPaymentIdAsync(msg.Provider, msg.ProviderPaymentId, context.CancellationToken);
        if (payment == null)
        {
            _logger.LogWarning("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Payment not found for ProviderPaymentId: {ProviderPaymentId}",
                DateTime.UtcNow, msg.ProviderPaymentId);
            return;
        }

        _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Found payment - PaymentId: {PaymentId}, BookingId: {BookingId}, CustomerId: {CustomerId}, Method: {Method}, Status: {Status}", 
            DateTime.UtcNow, payment.Id, payment.BookingId?.ToString() ?? "null", payment.CustomerId, payment.Method, payment.Status);

        // Only broadcast for booking payments (not cash, not top-ups)
        if (payment.Method == PaymentMethod.Cash)
        {
            _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Skipping broadcast - Payment method is Cash", 
                DateTime.UtcNow);
            return;
        }

        // Broadcast payment events even if bookingId is null (for PayOnline flow where booking is created after payment)
        // Top-ups don't have booking IDs, but we can distinguish them by checking if payment has a bookingId field set to null
        // vs top-ups which don't use this consumer (they use DriverTopUpWebhookConsumer)
        
        _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Checking event broadcaster - IsNull: {IsNull}", 
            DateTime.UtcNow, _eventBroadcaster == null);
        
        if (_eventBroadcaster != null)
        {
            try
            {
                var broadcastTime = DateTime.UtcNow;
                var payload = new BookingPaymentPaidPayload(
                    payment.Id,
                    payment.BookingId ?? Guid.Empty,  // Use Empty Guid if null (customer app will handle null)
                    payment.CustomerId,
                    payment.Amount,
                    "Paid",
                    payment.PaidAt ?? DateTime.UtcNow
                );
                
                _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Broadcasting payment paid event - PaymentId: {PaymentId}, BookingId: {BookingId}, CustomerId: {CustomerId}, Amount: {Amount}", 
                    broadcastTime, payload.PaymentId, payload.BookingId == Guid.Empty ? "null" : payload.BookingId.ToString(), payload.CustomerId, payload.Amount);
                
                await _eventBroadcaster.PublishPaidAsync(payload, context.CancellationToken);
                
                _logger.LogInformation("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Successfully broadcasted payment paid event - PaymentId: {PaymentId}, CustomerId: {CustomerId}", 
                    DateTime.UtcNow, payment.Id, payment.CustomerId);
            }
            catch (Exception ex)
            {
                // Deliberately swallowed. The SSE/SignalR broadcast is best-effort and ephemeral:
                // the payment is already persisted as Paid, and the SSE endpoint re-checks payment
                // status on connect (PaymentsController), so a client that missed the push still
                // learns the outcome.
                //
                // Rethrowing here failed the whole message for a transient transport blip, and
                // MassTransit then redelivered it (3 immediate retries + 3 delayed) - re-running
                // the receipt publish below and sending the customer duplicate receipt emails,
                // because BookingPaymentReceiptEmailConsumer has no dedupe of its own.
                _logger.LogError(ex, "[PAYMENT] [WEBHOOK_CONSUMER] Failed to broadcast payment paid event for payment {PaymentId}; continuing (broadcast is best-effort)",
                    payment.Id);
            }
        }
        else
        {
            _logger.LogWarning("[PAYMENT] [WEBHOOK_CONSUMER] [{Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC] Event broadcaster is null - cannot broadcast payment event", 
                DateTime.UtcNow);
        }

        // Request payment receipt email (consumer in Bookings resolves customer email and sends)
        await context.Publish(new BookingPaymentReceiptRequestedEvent
        {
            CustomerId = payment.CustomerId,
            PaymentNumber = payment.PaymentNumber,
            Amount = payment.Amount,
            Currency = payment.Currency,
            CheckoutUrl = payment.ProviderCheckoutUrl,
            PaidAt = payment.PaidAt ?? DateTime.UtcNow
        }, context.CancellationToken);
    }
}
