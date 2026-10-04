using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Consumers;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Validators;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// GitLab #51: a booking cancelled after the customer paid online used to leave the money where it
/// was — payment Paid, booking Cancelled, and nothing reconciling the two.
///
/// The invariant under test is what the consumer refuses to do. It ships with automatic refunds off
/// and only ever counts and warns; and even with them on it refunds a narrow slice — a cancellation
/// nobody caused, on a payment still inside the refund window. Everything else stays a human
/// decision, because an aged payment may already have been charged back with the customer's bank
/// and nothing in this system ingests disputes.
/// </summary>
public class CancelledBookingRefundTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public CancelledBookingRefundTests()
    {
        _mediator.Send(Arg.Any<RefundPaymentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
    }

    private BookingCancelledPaymentConsumer Consumer(bool autoRefund = false, int refundWindowHours = 168) => new(
        _repo,
        _mediator,
        Options.Create(new PaymentRefundOptions
        {
            AutoRefundCancelledBookings = autoRefund,
            RefundAllowedWithinHoursAfterPayment = refundWindowHours
        }),
        NullLogger<BookingCancelledPaymentConsumer>.Instance);

    private static ConsumeContext<BookingCancelledEvent> ContextFor(BookingCancelledEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<BookingCancelledEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    /// <param name="cancelledBy">Null is the platform-fault case: nobody accepted the booking.</param>
    private static BookingCancelledEvent Cancellation(Guid bookingId, Guid? cancelledBy = null, Guid? driverId = null)
        => new()
        {
            BookingId = bookingId,
            CustomerId = Guid.NewGuid(),
            CancelledAt = DateTime.UtcNow,
            CancellationReason = cancelledBy is null ? "No driver found" : "Changed my mind",
            CancelledBy = cancelledBy,
            SelectedDriverId = driverId
        };

    private Payment PaidPayment(Guid bookingId, decimal amount = 500m, DateTime? paidAt = null)
    {
        var payment = Payment.Create(bookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "inv-1", "https://invoice.url", payment.PaymentNumber);
        payment.SetProviderCaptureId("pr-1");
        payment.MarkAsPaid(paidAt ?? DateTime.UtcNow);
        _repo.Payments.Add(payment);
        return payment;
    }

    private async Task NoRefundIsRequested()
        => await _mediator.DidNotReceive().Send(Arg.Any<RefundPaymentCommand>(), Arg.Any<CancellationToken>());

    // --- No-ops: nothing to reconcile ---

    [Fact]
    public async Task A_cancelled_booking_with_no_payment_is_a_no_op()
    {
        await Consumer().Consume(ContextFor(Cancellation(Guid.NewGuid())));

        await NoRefundIsRequested();
    }

    [Fact]
    public async Task A_payment_that_was_never_paid_is_a_no_op()
    {
        var bookingId = Guid.NewGuid();
        var payment = Payment.Create(bookingId, Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        _repo.Payments.Add(payment);

        await Consumer(autoRefund: true).Consume(ContextFor(Cancellation(bookingId)));

        await NoRefundIsRequested();
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    /// <summary>Cash never went through a gateway, so there is nothing to send back through one.</summary>
    [Fact]
    public async Task A_cash_payment_is_a_no_op()
    {
        var bookingId = Guid.NewGuid();
        // CreateCashOnDelivery already lands Paid.
        _repo.Payments.Add(Payment.CreateCashOnDelivery(bookingId, Guid.NewGuid(), 500m));

        await Consumer(autoRefund: true).Consume(ContextFor(Cancellation(bookingId)));

        await NoRefundIsRequested();
    }

    /// <summary>
    /// Redelivery is the case this guards. MassTransit retries and redelivers, so the same
    /// cancellation can arrive twice; the first attempt leaves the payment Refunded or
    /// RefundPending, and the second must not start a second refund.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refund_already_resolved_or_in_flight_is_a_no_op(bool succeeded)
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        if (succeeded)
            payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 500m);
        else
            payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);

        await Consumer(autoRefund: true).Consume(ContextFor(Cancellation(bookingId)));

        await NoRefundIsRequested();
    }

    // --- Shipped behaviour: count and warn, never move money ---

    /// <summary>
    /// The default. Until PayMongo's refund behaviour is known and the volume is visible, the
    /// consumer's whole job is to make the backlog countable — it must not refund anything.
    /// </summary>
    [Fact]
    public async Task With_automatic_refunds_off_a_paid_cancelled_booking_is_only_flagged()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);

        await Consumer(autoRefund: false).Consume(ContextFor(Cancellation(bookingId)));

        await NoRefundIsRequested();
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    // --- With automation on ---

    [Fact]
    public async Task A_booking_nobody_accepted_is_refunded_without_bypassing_the_time_limit()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId);

        await Consumer(autoRefund: true).Consume(ContextFor(Cancellation(bookingId)));

        await _mediator.Received(1).Send(
            Arg.Is<RefundPaymentCommand>(c =>
                c.BookingId == bookingId
                && c.DriverId == null
                && c.BypassTimeLimit == false
                && c.Amount == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A customer who cancels after a driver was assigned may owe a cancellation fee. That is a
    /// pricing decision #51 leaves alone, so this path must not pre-empt it by refunding in full.
    /// </summary>
    [Fact]
    public async Task A_booking_cancelled_by_a_user_is_never_refunded_automatically()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId);

        await Consumer(autoRefund: true)
            .Consume(ContextFor(Cancellation(bookingId, cancelledBy: Guid.NewGuid(), driverId: Guid.NewGuid())));

        await NoRefundIsRequested();
    }

    /// <summary>
    /// The scheduled-booking trap. A delivery booked ten days out and paid immediately auto-cancels
    /// on day ten, by which point the payment is past the 168h refund window. Refunding it would
    /// need BypassTimeLimit — and a payment that old is also one the customer may already have
    /// disputed with their bank, which nothing here can detect. So it goes to a human instead.
    /// </summary>
    [Fact]
    public async Task A_payment_past_the_refund_window_is_flagged_rather_than_refunded()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId, paidAt: DateTime.UtcNow.AddHours(-200));

        await Consumer(autoRefund: true, refundWindowHours: 168)
            .Consume(ContextFor(Cancellation(bookingId)));

        await NoRefundIsRequested();
    }

    /// <summary>
    /// Mirrors RefundPaymentCommandHandler, where 0 disables the limit. The window is what decides
    /// automatic from manual here, so the two readings of it must not diverge.
    /// </summary>
    [Fact]
    public async Task A_disabled_refund_window_lets_an_old_payment_through()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId, paidAt: DateTime.UtcNow.AddHours(-5000));

        await Consumer(autoRefund: true, refundWindowHours: 0)
            .Consume(ContextFor(Cancellation(bookingId)));

        await _mediator.Received(1).Send(Arg.Any<RefundPaymentCommand>(), Arg.Any<CancellationToken>());
    }

    // --- The MediatR pipeline the consumer's command actually has to survive ---

    /// <summary>
    /// The tests above mock IMediator, which skips ValidationBehavior — and that pipeline throws
    /// rather than returning a failed Result, so a command it rejects would blow the consumer up,
    /// exhaust its retries and dead-letter, forever.
    ///
    /// This is the shape that matters: no driver was ever assigned, which is the whole reason this
    /// booking was cancelled. The validator has to accept it.
    /// </summary>
    [Fact]
    public void A_refund_for_a_booking_no_driver_accepted_survives_validation()
    {
        var result = new RefundPaymentCommandValidator().Validate(
            new RefundPaymentCommand(Guid.NewGuid(), null, "CANCELLATION"));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>
    /// The other half of that rule. Null means "there was no driver"; an all-zero Guid means a
    /// caller meant to name one and sent nothing, and letting it through would silently skip the
    /// wallet reversal on a booking a driver had actually been paid for.
    /// </summary>
    [Fact]
    public void A_refund_naming_an_all_zero_driver_is_still_rejected()
    {
        var result = new RefundPaymentCommandValidator().Validate(
            new RefundPaymentCommand(Guid.NewGuid(), Guid.Empty, "CANCELLATION"));

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// A refund the gateway refuses is exactly the silent failure #51 exists to stop: it happens
    /// inside a background consumer where nobody is looking. It must not throw either — throwing
    /// would send the message round the retry loop to be refused again.
    /// </summary>
    [Fact]
    public async Task A_refused_refund_is_reported_rather_than_thrown()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId);
        _mediator.Send(Arg.Any<RefundPaymentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail("Refund could not be completed."));

        var exception = await Record.ExceptionAsync(
            () => Consumer(autoRefund: true).Consume(ContextFor(Cancellation(bookingId))));

        Assert.Null(exception);
    }
}
