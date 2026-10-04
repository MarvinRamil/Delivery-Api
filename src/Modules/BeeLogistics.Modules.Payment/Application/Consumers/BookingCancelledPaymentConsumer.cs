using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Shared.Infrastructure;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Application.Consumers;

/// <summary>
/// Reconciles a cancelled booking against its online payment (GitLab #51).
///
/// Before this existed, cancelling a paid booking did nothing to the money: the payment stayed
/// Paid, the booking went Cancelled, and the two never met unless someone noticed by hand. #50
/// made that visible to customers - bookings nobody accepted now correctly reach Cancelled, so the
/// customer sees "Cancelled" and reasonably expects a refund.
///
/// What this does by default is <b>count and alert, not refund</b>. See
/// <see cref="PaymentRefundOptions.AutoRefundCancelledBookings"/> for why it ships that way.
/// </summary>
/// <remarks>
/// Injects IBus-free dependencies only. Note the absence of IPublishEndpoint: it stages onto the
/// BookingsDbContext outbox, which this module never saves, so the message would be dropped.
/// PaymentModuleOutboxConventionTests fails the build if that is reintroduced (GitLab #27).
/// </remarks>
public class BookingCancelledPaymentConsumer : IConsumer<BookingCancelledEvent>
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IMediator _mediator;
    private readonly PaymentRefundOptions _refundOptions;
    private readonly ILogger<BookingCancelledPaymentConsumer> _logger;

    public BookingCancelledPaymentConsumer(
        IPaymentRepository paymentRepository,
        IMediator mediator,
        IOptions<PaymentRefundOptions> refundOptions,
        ILogger<BookingCancelledPaymentConsumer> logger)
    {
        _paymentRepository = paymentRepository;
        _mediator = mediator;
        _refundOptions = refundOptions.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingCancelledEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var payment = await _paymentRepository.GetByBookingIdAsync(msg.BookingId, ct);
        if (payment == null)
        {
            _logger.LogDebug("[CancelledBookingPayment] Booking {BookingId} cancelled with no linked payment, nothing to reconcile.", msg.BookingId);
            return;
        }

        // Anything already resolved, in flight, or never collected online is a no-op. Redelivery
        // lands here too: a refund started on the first attempt leaves the payment Refunded or
        // RefundPending, so a retried message cannot refund twice.
        if (payment.Status is PaymentStatus.Refunded or PaymentStatus.RefundPending)
        {
            _logger.LogDebug(
                "[CancelledBookingPayment] Payment {PaymentId} for booking {BookingId} is already {Status}, nothing to reconcile.",
                payment.Id, msg.BookingId, payment.Status);
            return;
        }

        if (payment.Status != PaymentStatus.Paid)
        {
            _logger.LogDebug(
                "[CancelledBookingPayment] Payment {PaymentId} for booking {BookingId} is {Status}, not Paid - no money to return.",
                payment.Id, msg.BookingId, payment.Status);
            return;
        }

        if (payment.Method == PaymentMethod.Cash)
        {
            _logger.LogDebug(
                "[CancelledBookingPayment] Payment {PaymentId} for booking {BookingId} was cash; settled off-platform.",
                payment.Id, msg.BookingId);
            return;
        }

        // Nobody chose to cancel this - the search ran out of road. Unambiguously our failure, and
        // the only case an automatic refund is defensible for. A customer cancelling after a driver
        // was assigned may owe a cancellation fee, which is a pricing decision this issue leaves alone.
        var isPlatformFault = msg.CancelledBy is null;

        // The refund window doubles as the automatic/manual boundary. A payment old enough to need
        // BypassTimeLimit is also old enough that the customer may already have charged it back with
        // their bank - and nothing in this system ingests disputes, so we cannot tell. Refunding it
        // automatically risks paying twice, so it goes to a human who can check the provider dashboard.
        var isWithinRefundWindow = IsWithinRefundWindow(payment.PaidAt);

        if (!_refundOptions.AutoRefundCancelledBookings)
        {
            FlagForManualResolution(msg, payment, isPlatformFault, "automatic refunds are switched off");
            return;
        }

        if (!isPlatformFault)
        {
            FlagForManualResolution(msg, payment, isPlatformFault, "cancelled by a user, so a cancellation fee may apply");
            return;
        }

        if (!isWithinRefundWindow)
        {
            FlagForManualResolution(msg, payment, isPlatformFault,
                $"paid more than {_refundOptions.RefundAllowedWithinHoursAfterPayment}h ago, needs a dispute check first");
            return;
        }

        // BypassTimeLimit stays false on purpose: it means "a human has looked at this", and the
        // guard above has already excluded everything that would need it.
        //
        // SelectedDriverId is passed through as-is, null included. This path only runs when nobody
        // accepted the booking, so it is normally null - and the command's validator accepts null
        // while still rejecting an all-zero Guid from a caller who meant to name someone.
        var result = await _mediator.Send(
            new RefundPaymentCommand(
                msg.BookingId,
                msg.SelectedDriverId,
                "CANCELLATION",
                Amount: null,
                BypassTimeLimit: false),
            ct);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "[CancelledBookingPayment] Refunded {Amount} {Currency} for cancelled booking {BookingId} (payment {PaymentId}, nobody accepted it).",
                payment.RefundableAmount, payment.Currency, msg.BookingId, payment.Id);
            return;
        }

        // A refused automatic refund is exactly the silent failure this issue exists to stop.
        BeeMetrics.PaymentsCancelledUnrefunded.Add(1, new KeyValuePair<string, object?>("source", "refund_failed"));
        _logger.LogWarning(
            "[CancelledBookingPayment] Automatic refund refused for booking {BookingId} (payment {PaymentId}, {Amount} {Currency}): {Error}. Needs manual resolution via POST /api/payments/refund.",
            msg.BookingId, payment.Id, payment.RefundableAmount, payment.Currency, result.Error);
    }

    private bool IsWithinRefundWindow(DateTime? paidAt)
    {
        // Mirrors RefundPaymentCommandHandler: 0 disables the limit, and an unknown PaidAt is not
        // treated as expired there either.
        if (_refundOptions.RefundAllowedWithinHoursAfterPayment <= 0 || !paidAt.HasValue)
            return true;

        return DateTime.UtcNow <= paidAt.Value.AddHours(_refundOptions.RefundAllowedWithinHoursAfterPayment);
    }

    private void FlagForManualResolution(
        BookingCancelledEvent msg,
        Domain.Payment payment,
        bool isPlatformFault,
        string why)
    {
        BeeMetrics.PaymentsCancelledUnrefunded.Add(1, new KeyValuePair<string, object?>("source", "event"));
        _logger.LogWarning(
            "[CancelledBookingPayment] Booking {BookingId} cancelled with {Amount} {Currency} still held on payment {PaymentId} - {Why}. Platform fault: {IsPlatformFault}. Reason: {CancellationReason}. Refund manually via POST /api/payments/refund.",
            msg.BookingId, payment.RefundableAmount, payment.Currency, payment.Id, why, isPlatformFault, msg.CancellationReason);
    }
}
