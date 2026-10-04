using Microsoft.Extensions.Logging.Abstractions;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Tests.Fakes;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Provider-scoped webhook lookups: an id delivered by one provider must never
/// match a payment owned by the other, even if the raw ids happened to collide.
/// </summary>
public class CrossProviderWebhookTests
{
    [Fact]
    public async Task PayMongo_webhook_never_matches_a_xendit_payment_with_the_same_id()
    {
        var repo = new FakePaymentRepository();
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "shared-id", "https://invoice.url", payment.PaymentNumber);
        repo.Payments.Add(payment);

        var result = await new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance)
            .Handle(new ProcessWebhookCommand(PaymentProviders.PayMongo, "shared-id", "PAID", DateTime.UtcNow), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task PayMongo_paid_webhook_marks_paymongo_payment_paid()
    {
        var repo = new FakePaymentRepository();
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.PayMongo, "cs_1", "https://checkout.url", payment.PaymentNumber);
        repo.Payments.Add(payment);

        var result = await new ProcessWebhookHandler(repo, NullLogger<ProcessWebhookHandler>.Instance)
            .Handle(new ProcessWebhookCommand(PaymentProviders.PayMongo, "cs_1", "PAID", DateTime.UtcNow), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public async Task Refund_webhook_is_scoped_to_its_provider()
    {
        var repo = new FakePaymentRepository();
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "inv-1", "https://invoice.url", payment.PaymentNumber);
        payment.MarkAsPaid(DateTime.UtcNow);
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 500m);
        repo.Payments.Add(payment);

        var result = await new ProcessRefundWebhookHandler(repo,
                NSubstitute.Substitute.For<BeeLogistics.Modules.Payment.Application.Interfaces.IPaymentOutboxPublisher>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessRefundWebhookHandler>.Instance)
            .Handle(new ProcessRefundWebhookCommand(PaymentProviders.PayMongo, "rf-1", "SUCCEEDED", null), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
    }
}
