using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Interfaces;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// GitLab #65. With Payment's own outbox, <c>IPaymentOutboxPublisher.Publish</c> does not send
/// anything — it stages a row that is committed by the next <c>SaveChanges</c>. So the ordering
/// is load-bearing: a message staged *after* the last save is never flushed and the event is
/// silently lost, which is exactly the failure #27 was about, reached from the other direction.
///
/// These handlers previously published after their save on purpose, so that a failed persist
/// could not emit a wallet reversal. The outbox gives that guarantee properly — staged message
/// and payment row commit in one transaction — but only if the publish comes first.
///
/// PaymentRefundedEvent is the message that reverses a driver's wallet after a customer refund,
/// so a dropped publish here means the platform eats the money.
/// </summary>
public class PaymentOutboxPublishOrderingTests
{
    private readonly IPaymentRepository _repo = Substitute.For<IPaymentRepository>();
    private readonly IPaymentOutboxPublisher _outboxPublisher = Substitute.For<IPaymentOutboxPublisher>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<string> _calls = new();

    public PaymentOutboxPublishOrderingTests()
    {
        _outboxPublisher
            .When(p => p.Publish(Arg.Any<PaymentRefundedEvent>()))
            .Do(_ => _calls.Add("publish"));

        _repo.When(r => r.SaveChangesAsync(Arg.Any<CancellationToken>()))
             .Do(_ => _calls.Add("save"));
    }

    private static Payment RefundPendingPayment(Guid bookingId, Guid driverId, decimal amount = 500m)
    {
        var payment = Payment.Create(bookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.PayMongo, "cs_1", "https://checkout.url", payment.PaymentNumber);
        payment.SetProviderCaptureId("pay_1");
        payment.MarkAsPaid(DateTime.UtcNow);
        payment.MarkAsRefundPending("rf_1", driverId, amount);
        return payment;
    }

    // --- The webhook that finalises an asynchronous refund ---

    [Fact]
    public async Task The_refund_webhook_stages_the_wallet_reversal_before_it_saves()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        _repo.GetByProviderRefundIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(RefundPendingPayment(bookingId, driverId));

        var handler = new ProcessRefundWebhookHandler(
            _repo, _outboxPublisher, NullLogger<ProcessRefundWebhookHandler>.Instance);

        var result = await handler.Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.PayMongo, "rf_1", "SUCCEEDED", null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "publish", "save" }, _calls.ToArray());
    }

    [Fact]
    public async Task The_refund_webhook_reverses_the_amount_that_refund_moved()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        _repo.GetByProviderRefundIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(RefundPendingPayment(bookingId, driverId, amount: 320m));

        var handler = new ProcessRefundWebhookHandler(
            _repo, _outboxPublisher, NullLogger<ProcessRefundWebhookHandler>.Instance);

        await handler.Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.PayMongo, "rf_1", "SUCCEEDED", null),
            CancellationToken.None);

        _outboxPublisher.Received(1).Publish(
            Arg.Is<PaymentRefundedEvent>(e =>
                e.BookingId == bookingId && e.DriverId == driverId && e.AmountRefunded == 320m));
    }

    /// <summary>Redelivery must not emit a second wallet reversal.</summary>
    [Fact]
    public async Task A_redelivered_refund_webhook_stages_nothing()
    {
        var payment = RefundPendingPayment(Guid.NewGuid(), Guid.NewGuid());
        payment.MarkAsRefunded(DateTime.UtcNow, "rf_1", 500m);
        _repo.GetByProviderRefundIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(payment);

        var handler = new ProcessRefundWebhookHandler(
            _repo, _outboxPublisher, NullLogger<ProcessRefundWebhookHandler>.Instance);

        await handler.Handle(
            new ProcessRefundWebhookCommand(PaymentProviders.PayMongo, "rf_1", "SUCCEEDED", null),
            CancellationToken.None);

        Assert.Empty(_calls);
    }

    // --- The synchronous refund path, including its concurrency retry ---

    private RefundPaymentCommandHandler SynchronousRefundHandler(Payment payment, GatewayRefundStatus status)
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.ProviderName.Returns(PaymentProviders.PayMongo);
        gateway.CreateRefundAsync(Arg.Any<CreateGatewayRefundRequest>(), Arg.Any<CancellationToken>())
               .Returns(new GatewayRefund("rf_1", "pay_1", status, status.ToString(), payment.Amount, "PHP"));

        var factory = Substitute.For<IPaymentGatewayFactory>();
        factory.Get(Arg.Any<string>()).Returns(gateway);
        factory.GetActive().Returns(gateway);

        _repo.GetByBookingIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(payment);
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(payment);

        return new RefundPaymentCommandHandler(
            _repo,
            factory,
            _outboxPublisher,
            _mediator,
            Options.Create(new PaymentRefundOptions { RefundAllowedWithinHoursAfterPayment = 0 }),
            NullLogger<RefundPaymentCommandHandler>.Instance);
    }

    private static Payment PaidPayment(Guid bookingId, decimal amount = 500m)
    {
        var payment = Payment.Create(bookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.PayMongo, "cs_1", "https://checkout.url", payment.PaymentNumber);
        payment.SetProviderCaptureId("pay_1");
        payment.MarkAsPaid(DateTime.UtcNow);
        return payment;
    }

    [Fact]
    public async Task An_immediately_succeeded_refund_stages_before_it_saves()
    {
        var bookingId = Guid.NewGuid();
        var handler = SynchronousRefundHandler(PaidPayment(bookingId), GatewayRefundStatus.Succeeded);

        var result = await handler.Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), "CANCELLATION"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        // Leading "save" is the pre-flight reservation (RefundPending flip) that now happens
        // before the gateway is ever called; "publish", "save" is the finalization this test is
        // actually about.
        Assert.Equal(new[] { "save", "publish", "save" }, _calls.ToArray());
    }

    /// <summary>
    /// The subtle one. On an xmin conflict the handler re-reads and saves again, so the event has
    /// to be staged on *each* attempt: a failed SaveChanges rolls back the staged outbox row along
    /// with everything else, and staging only once up front would commit a refund with no event —
    /// the driver's wallet never reversed, and nothing to show it was meant to be.
    /// </summary>
    [Fact]
    public async Task A_concurrency_conflict_restages_the_event_so_the_retry_still_carries_it()
    {
        var bookingId = Guid.NewGuid();
        var handler = SynchronousRefundHandler(PaidPayment(bookingId), GatewayRefundStatus.Succeeded);

        // Only the throw here: the constructor already records "save", and NSubstitute runs every
        // registered Do callback, so recording again would double-count each call. Attempt 1 is the
        // pre-flight reservation save (must succeed, or the refund never reaches the gateway at
        // all) - the conflict under test belongs to attempt 2, the first finalization save.
        var attempt = 0;
        _repo.When(r => r.SaveChangesAsync(Arg.Any<CancellationToken>())).Do(_ =>
        {
            if (++attempt == 2)
                throw new DbUpdateConcurrencyException("simulated xmin conflict");
        });

        var result = await handler.Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), "CANCELLATION"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new[] { "save", "publish", "save", "publish", "save" }, _calls.ToArray());
    }

    /// <summary>
    /// A refund the provider only accepted (Pending) must not reverse the wallet yet — the
    /// outcome arrives later on the refund webhook, and a refund that ultimately fails must never
    /// have moved driver money.
    /// </summary>
    [Fact]
    public async Task A_pending_refund_stages_no_wallet_reversal()
    {
        var bookingId = Guid.NewGuid();
        var handler = SynchronousRefundHandler(PaidPayment(bookingId), GatewayRefundStatus.Pending);

        await handler.Handle(
            new RefundPaymentCommand(bookingId, Guid.NewGuid(), "CANCELLATION"), CancellationToken.None);

        Assert.DoesNotContain("publish", _calls);
        _outboxPublisher.DidNotReceive().Publish(Arg.Any<PaymentRefundedEvent>());
    }
}
