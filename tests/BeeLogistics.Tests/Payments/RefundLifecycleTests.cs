using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Refund lifecycle: initiation (RefundPaymentCommandHandler) and asynchronous
/// completion via Xendit's refund.* webhook (ProcessRefundWebhookHandler).
/// The invariant under test: the driver wallet debit (PaymentRefundedEvent) is
/// published exactly once, and only when the refund actually succeeded.
/// </summary>
public class RefundLifecycleTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();
    // Payment's own hand-rolled outbox (not MassTransit's - see IPaymentOutboxPublisher's doc
    // comment). Mocking the same abstraction the handler injects is what keeps this suite honest.
    private readonly IPaymentOutboxPublisher _outboxPublisher = Substitute.For<IPaymentOutboxPublisher>();
    // Defaults to returning null for GetBookingSelectedDriverIdQuery (fail-open: unknown
    // assignment never blocks a refund), matching every existing test here which has no real
    // booking behind it. Tests that exercise the cross-check override this per-call.
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public RefundLifecycleTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.Xendit);
        _factory.Get(Arg.Any<string>()).Returns(_gateway);
        _factory.GetActive().Returns(_gateway);
    }

    private RefundPaymentCommandHandler InitiationHandler(int refundWindowHours = 0) => new(
        _repo,
        _factory,
        _outboxPublisher,
        _mediator,
        Options.Create(new PaymentRefundOptions { RefundAllowedWithinHoursAfterPayment = refundWindowHours }),
        NullLogger<RefundPaymentCommandHandler>.Instance);

    private ProcessRefundWebhookHandler WebhookHandler() => new(
        _repo, _outboxPublisher, NullLogger<ProcessRefundWebhookHandler>.Instance);

    private Payment PaidPayment(Guid bookingId, decimal amount = 500m)
    {
        var payment = Payment.Create(bookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "inv-1", "https://invoice.url", payment.PaymentNumber);
        payment.SetProviderCaptureId("pr-1");
        payment.MarkAsPaid(DateTime.UtcNow);
        _repo.Payments.Add(payment);
        return payment;
    }

    private void XenditReturnsRefund(string status) =>
        _gateway.CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayRefund(
                "rf-1", "pr-1",
                status.Equals("SUCCEEDED", StringComparison.OrdinalIgnoreCase)
                    ? GatewayRefundStatus.Succeeded
                    : GatewayRefundStatus.Pending,
                status, 500m, "PHP"));

    // --- Out-of-order refund webhooks (GitLab #38) ---

    /// <summary>
    /// The regression test for #38. Provider webhooks are redelivered and unordered, so a FAILED
    /// landing before a SUCCEEDED for the same refund id is a delivery artefact, not a state flip.
    ///
    /// It is not short-circuited either: the SUCCEEDED branch guards on Status == Refunded, and
    /// after a failure the status is Paid. So it runs - and while MarkRefundFailed cleared the
    /// in-flight context, it ran with no driver to debit and fell back to the full payment amount.
    /// The customer had their money back and the driver kept their earnings.
    /// </summary>
    [Fact]
    public async Task Succeeded_webhook_after_a_failed_one_still_debits_the_original_driver()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var payment = PaidPayment(bookingId, amount: 500m);
        payment.MarkAsRefundPending("rf-1", driverId, 200m); // partial refund
        var handler = WebhookHandler();

        await handler.Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "FAILED", "TEMPORARY_ERROR"), CancellationToken.None);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        _outboxPublisher.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>());

        // The delayed SUCCEEDED for the same refund arrives.
        var result = await handler.Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "SUCCEEDED", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        _outboxPublisher.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e =>
                e.BookingId == bookingId &&
                e.DriverId == driverId &&
                e.AmountRefunded == 200m));   // the partial refund, not the full payment
    }

    /// <summary>
    /// The same sequence must not inflate the accumulator: only ₱200 actually moved.
    /// </summary>
    [Fact]
    public async Task Succeeded_after_failed_records_only_the_amount_that_moved()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId, amount: 500m);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);
        var handler = WebhookHandler();

        await handler.Handle(new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "FAILED", "TEMPORARY_ERROR"), CancellationToken.None);
        await handler.Handle(new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "SUCCEEDED", null), CancellationToken.None);

        Assert.Equal(200m, payment.TotalRefunded);
        Assert.Equal(300m, payment.RefundableAmount);
    }

    /// <summary>
    /// The pre-existing guarantee that must not regress while fixing the above: a failure on its
    /// own never moves driver money.
    /// </summary>
    [Fact]
    public async Task A_failed_refund_alone_still_never_debits_the_driver()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);

        await WebhookHandler().Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "FAILED", "INSUFFICIENT_BALANCE"), CancellationToken.None);

        _outboxPublisher.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>());
        Assert.Equal(0m, payment.TotalRefunded);
        Assert.Equal(500m, payment.RefundableAmount);
    }

    // --- Refund cap (GitLab #34) ---

    /// <summary>
    /// The cap is measured against what is still refundable, not the original amount. It used to
    /// hold only incidentally, because the status guards short-circuit Refunded and RefundPending -
    /// so it was the state machine blocking a second refund, not the check.
    /// </summary>
    [Fact]
    public async Task Refund_exceeding_the_refundable_balance_is_refused()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId, amount: 500m);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), Amount: 500.01m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("refundable balance", result.Error);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public async Task Refund_of_zero_or_less_is_refused()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), Amount: 0m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cap has to stand on its own once the status guard is out of the way. Reaching this state
    /// requires a prior refund that was recorded and then failed back to Paid, which is exactly the
    /// sequence MarkRefundFailed produces.
    /// </summary>
    [Fact]
    public async Task Cap_shrinks_after_a_partial_refund_even_once_status_returns_to_paid()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId, amount: 500m);
        payment.MarkAsRefunded(DateTime.UtcNow, "rf-0", 300m);
        payment.MarkRefundFailed("returned to Paid for a follow-up refund");

        Assert.Equal(200m, payment.RefundableAmount);

        var overCap = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), Amount: 250m), CancellationToken.None);

        Assert.False(overCap.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A refund with no explicit amount means "refund what is left", not "refund the whole payment".
    /// </summary>
    [Fact]
    public async Task Unspecified_amount_refunds_the_remaining_balance()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId, amount: 500m);
        payment.MarkAsRefunded(DateTime.UtcNow, "rf-0", 300m);
        payment.MarkRefundFailed("returned to Paid for a follow-up refund");
        XenditReturnsRefund("SUCCEEDED");

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _gateway.Received(1).CreateRefundAsync(
            Arg.Is<CreateGatewayRefundRequest>(r => r.Amount == 200m), Arg.Any<CancellationToken>());
        Assert.Equal(500m, payment.TotalRefunded);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    // --- Initiation ---

    [Fact]
    public async Task Pending_refund_sets_RefundPending_and_does_not_debit_driver()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        XenditReturnsRefund("PENDING");

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal("rf-1", payment.ProviderRefundId);
        Assert.Equal(driverId, payment.RefundDriverId);
        Assert.Equal(500m, payment.RefundAmount);
        _outboxPublisher.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>());
    }

    [Fact]
    public async Task Synchronously_succeeded_refund_marks_refunded_and_publishes_event()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        XenditReturnsRefund("SUCCEEDED");

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        _outboxPublisher.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e => e.BookingId == bookingId && e.DriverId == driverId && e.AmountRefunded == 500m));
    }

    [Fact]
    public async Task Refund_while_already_pending_is_idempotent_and_does_not_call_xendit_again()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refund_of_already_refunded_payment_is_idempotent()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1");

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cash_payment_cannot_be_refunded()
    {
        var bookingId = Guid.NewGuid();
        var payment = Payment.CreateCashOnDelivery(bookingId, Guid.NewGuid(), 500m);
        _repo.Payments.Add(payment);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Refund_amount_exceeding_payment_is_rejected()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId, amount: 500m);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), Amount: 600m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refund_outside_time_window_is_rejected_unless_bypassed()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsPaid(DateTime.UtcNow.AddHours(-10));
        XenditReturnsRefund("PENDING");

        var blocked = await InitiationHandler(refundWindowHours: 1).Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);
        Assert.False(blocked.IsSuccess);

        var bypassed = await InitiationHandler(refundWindowHours: 1).Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), BypassTimeLimit: true), CancellationToken.None);
        Assert.True(bypassed.IsSuccess);
    }

    [Fact]
    public async Task Refund_routes_to_the_gateway_that_collected_the_payment_not_the_active_one()
    {
        // Switch-safety invariant: a pre-switch Xendit payment must refund via
        // Xendit even when PayMongo is the active gateway.
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId); // Provider = xendit
        XenditReturnsRefund("PENDING");

        var activeGateway = Substitute.For<IPaymentGateway>();
        activeGateway.ProviderName.Returns(PaymentProviders.PayMongo);
        _factory.GetActive().Returns(activeGateway);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        _factory.Received(1).Get(PaymentProviders.Xendit);
        await _gateway.Received(1).CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
        await activeGateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    // --- Webhook completion ---

    [Fact]
    public async Task Succeeded_webhook_finalizes_refund_and_debits_driver_with_stored_context()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefundPending("rf-1", driverId, 200m); // partial refund

        var result = await WebhookHandler().Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "SUCCEEDED", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        _outboxPublisher.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e => e.BookingId == bookingId && e.DriverId == driverId && e.AmountRefunded == 200m));
    }

    [Fact]
    public async Task Redelivered_succeeded_webhook_does_not_debit_driver_twice()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);
        var handler = WebhookHandler();

        await handler.Handle(new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "SUCCEEDED", null), CancellationToken.None);
        await handler.Handle(new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "SUCCEEDED", null), CancellationToken.None);

        _outboxPublisher.Received(1).Publish(Arg.Any<PaymentRefundedEvent>());
    }

    [Fact]
    public async Task Failed_webhook_returns_payment_to_paid_and_never_debits_driver()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);

        var result = await WebhookHandler().Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-1", "FAILED", "INSUFFICIENT_BALANCE"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Contains("INSUFFICIENT_BALANCE", payment.FailureReason);
        _outboxPublisher.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>());
    }

    [Fact]
    public async Task Webhook_for_unknown_refund_returns_failure()
    {
        var result = await WebhookHandler().Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.Xendit, "rf-unknown", "SUCCEEDED", null), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    // --- Reservation / race condition (concurrent refund triggers) ---

    /// <summary>
    /// The reservation (flip to RefundPending) happens before the gateway is ever called, and is
    /// guarded by the same xmin conflict handling as the rest of the handler. A second concurrent
    /// caller that lost the race must fail cleanly here, never reach the provider.
    /// </summary>
    [Fact]
    public async Task A_concurrency_conflict_reserving_the_refund_fails_without_calling_the_gateway()
    {
        var bookingId = Guid.NewGuid();
        PaidPayment(bookingId);
        _repo.FailNextSaveWithConcurrencyConflict = true;

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An ambiguous gateway failure (thrown exception - timeout, connection reset) must not revert
    /// the reservation to Paid: the provider may already have processed the refund, and PayMongo
    /// has no idempotency key to protect a retry from double-refunding. The payment stays
    /// RefundPending for the reconciliation job to resolve against the provider's own record.
    /// </summary>
    [Fact]
    public async Task Gateway_throwing_leaves_the_payment_reserved_rather_than_reverting_to_paid()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        _gateway.CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>())
            .Returns<GatewayRefund?>(_ => throw new HttpRequestException("simulated timeout"));

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
    }

    /// <summary>
    /// A clean rejection (a response was received, the provider said no) is unambiguous - safe to
    /// release the reservation so the payment can be retried.
    /// </summary>
    [Fact]
    public async Task Gateway_returning_null_releases_the_reservation_back_to_paid()
    {
        var bookingId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        _gateway.CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>())
            .Returns((GatewayRefund?)null);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid()), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    // --- Server-side driver cross-check ---

    [Fact]
    public async Task A_driver_id_that_disagrees_with_the_bookings_actual_assignment_is_rejected()
    {
        var bookingId = Guid.NewGuid();
        var assignedDriverId = Guid.NewGuid();
        var wrongDriverId = Guid.NewGuid();
        var payment = PaidPayment(bookingId);
        _mediator.Send(Arg.Is<GetBookingSelectedDriverIdQuery>(q => q.BookingId == bookingId), Arg.Any<CancellationToken>())
            .Returns((Guid?)assignedDriverId);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, wrongDriverId), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        await _gateway.DidNotReceive().CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_driver_id_matching_the_bookings_actual_assignment_is_accepted()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        PaidPayment(bookingId);
        XenditReturnsRefund("SUCCEEDED");
        _mediator.Send(Arg.Is<GetBookingSelectedDriverIdQuery>(q => q.BookingId == bookingId), Arg.Any<CancellationToken>())
            .Returns((Guid?)driverId);

        var result = await InitiationHandler().Handle(
            new RefundPaymentCommand(bookingId, driverId), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }
}
