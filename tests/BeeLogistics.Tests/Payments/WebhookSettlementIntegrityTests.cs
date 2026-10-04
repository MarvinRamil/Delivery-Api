using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Settlement integrity for invoice webhooks (GitLab #25):
///   1. a webhook must not settle a payment for an amount other than the one on record;
///   2. a late or out-of-order EXPIRED/FAILED must not drag a paid payment back out of Paid.
///
/// (2) is reachable in ordinary operation because PayMongoWebhooksController maps every non-paid
/// checkout event to the string "EXPIRED".
/// </summary>
public class WebhookSettlementIntegrityTests
{
    private readonly FakePaymentRepository _repo = new();

    private ProcessWebhookHandler Handler() => new(_repo, NullLogger<ProcessWebhookHandler>.Instance);

    private Payment PendingPayment(decimal amount = 500m, string invoiceId = "inv-1")
    {
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, invoiceId, "https://invoice.url", payment.PaymentNumber);
        _repo.Payments.Add(payment);
        return payment;
    }

    private Task<Result> Webhook(string status, decimal? paidAmount = null, DateTime? paidAt = null, string invoiceId = "inv-1")
        => Handler().Handle(
            new ProcessWebhookCommand(PaymentProviders.Xendit, invoiceId, status, paidAt, paidAmount),
            CancellationToken.None);

    // --- amount integrity ---

    [Fact]
    public async Task Settles_when_the_reported_amount_matches()
    {
        var payment = PendingPayment(amount: 500m);

        var result = await Webhook("PAID", paidAmount: 500m);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Theory]
    [InlineData(1)]        // paid far less than owed
    [InlineData(499.99)]   // one centavo short
    [InlineData(5000)]     // more than owed - equally suspicious
    public async Task Refuses_to_settle_when_the_reported_amount_differs(decimal reported)
    {
        var payment = PendingPayment(amount: 500m);

        var result = await Webhook("PAID", paidAmount: reported);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    /// <summary>
    /// Not every provider payload carries an amount; when it is absent the status transition must
    /// still apply, or real settlements would stop working.
    /// </summary>
    [Fact]
    public async Task Settles_when_the_provider_reports_no_amount()
    {
        var payment = PendingPayment(amount: 500m);

        var result = await Webhook("PAID", paidAmount: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    /// <summary>
    /// A mismatch is permanent - redelivery cannot fix it - so it must be distinguishable from the
    /// benign "this webhook was a driver top-up" case, which is what NotFound means here.
    /// </summary>
    [Fact]
    public async Task Amount_mismatch_is_not_reported_as_not_found()
    {
        PendingPayment(amount: 500m);

        var mismatch = await Webhook("PAID", paidAmount: 1m);
        var unknownInvoice = await Webhook("PAID", paidAmount: 500m, invoiceId: "inv-missing");

        Assert.Equal(ResultErrorKind.Failure, mismatch.ErrorKind);
        Assert.Equal(ResultErrorKind.NotFound, unknownInvoice.ErrorKind);
    }

    // --- status regression ---

    [Theory]
    [InlineData("EXPIRED")]
    [InlineData("FAILED")]
    public async Task Late_terminal_event_does_not_downgrade_a_paid_payment(string lateStatus)
    {
        var payment = PendingPayment();
        var paidAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await Webhook("PAID", paidAmount: 500m, paidAt: paidAt);

        var result = await Webhook(lateStatus);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(paidAt, payment.PaidAt);
    }

    [Fact]
    public async Task Late_terminal_event_does_not_downgrade_a_refunded_payment()
    {
        var payment = PendingPayment();
        await Webhook("PAID", paidAmount: 500m);
        payment.MarkAsRefunded();

        await Webhook("EXPIRED");

        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public async Task Refund_in_flight_is_not_downgraded_either()
    {
        var payment = PendingPayment();
        await Webhook("PAID", paidAmount: 500m);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);

        await Webhook("FAILED");

        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
    }

    /// <summary>
    /// The guard must only protect payments that actually reached a money state - an abandoned
    /// checkout still has to expire.
    /// </summary>
    [Theory]
    [InlineData("EXPIRED", PaymentStatus.Expired)]
    [InlineData("FAILED", PaymentStatus.Failed)]
    public async Task Pending_payment_still_reaches_its_terminal_state(string status, PaymentStatus expected)
    {
        var payment = PendingPayment();

        var result = await Webhook(status);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, payment.Status);
    }
}
