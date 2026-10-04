using Microsoft.Extensions.Logging.Abstractions;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Tests.Fakes;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Invoice webhook processing. Webhook failures now return 500 to Xendit, which
/// redelivers - so these handlers must be idempotent under duplicate delivery.
/// </summary>
public class ProcessWebhookHandlerTests
{
    private static Payment NewInvoicedPayment(FakePaymentRepository repo, string invoiceId = "inv-1")
    {
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, invoiceId, "https://invoice.url", payment.PaymentNumber);
        repo.Payments.Add(payment);
        return payment;
    }

    [Fact]
    public async Task Paid_webhook_marks_payment_paid()
    {
        var repo = new FakePaymentRepository();
        var payment = NewInvoicedPayment(repo);
        var paidAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var result = await new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance)
            .Handle(new ProcessWebhookCommand(PaymentProviders.Xendit, "inv-1", "PAID", paidAt), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(paidAt, payment.PaidAt);
    }

    [Fact]
    public async Task Duplicate_paid_webhook_does_not_overwrite_original_paid_timestamp()
    {
        var repo = new FakePaymentRepository();
        var payment = NewInvoicedPayment(repo);
        var firstPaidAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var handler = new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance);

        await handler.Handle(new ProcessWebhookCommand(PaymentProviders.Xendit, "inv-1", "PAID", firstPaidAt), CancellationToken.None);
        await handler.Handle(new ProcessWebhookCommand(PaymentProviders.Xendit, "inv-1", "SETTLED", firstPaidAt.AddMinutes(10)), CancellationToken.None);

        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(firstPaidAt, payment.PaidAt);
    }

    [Fact]
    public async Task Expired_webhook_marks_payment_expired()
    {
        var repo = new FakePaymentRepository();
        var payment = NewInvoicedPayment(repo);

        var result = await new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance)
            .Handle(new ProcessWebhookCommand(PaymentProviders.Xendit, "inv-1", "EXPIRED", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Expired, payment.Status);
    }

    [Fact]
    public async Task Unknown_invoice_returns_failure()
    {
        var repo = new FakePaymentRepository();

        var result = await new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance)
            .Handle(new ProcessWebhookCommand(PaymentProviders.Xendit, "inv-missing", "PAID", DateTime.UtcNow), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }
}
