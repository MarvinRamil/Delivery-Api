using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Tests.Fakes;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Stuck-refund resolution: a payment left in RefundPending because its refund.* webhook never
/// arrived - previously detection-only (metric + log). This asks the provider directly via
/// GetRefundAsync, then dispatches the finalization through the same ProcessRefundWebhookCommand a
/// real webhook would use, rather than reimplementing the state transitions.
/// </summary>
public class StuckRefundResolverTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    public StuckRefundResolverTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.Xendit);
        _factory.Get(Arg.Any<string>()).Returns(_gateway);
        _factory.GetActive().Returns(_gateway);
        _mediator.Send(Arg.Any<ProcessRefundWebhookCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
    }

    private StuckRefundResolver Resolver() => new(_repo, _factory, _mediator, NullLogger<StuckRefundResolver>.Instance);

    private PaymentEntity StuckPayment(string providerRefundId = "rf-1", int ageHours = 8)
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "inv-1", "https://invoice.url", payment.PaymentNumber);
        payment.SetProviderCaptureId("pr-1");
        payment.MarkAsPaid(Now.AddHours(-ageHours - 1));
        payment.MarkAsRefundPending(providerRefundId, Guid.NewGuid(), 500m);
        SetUpdatedAt(payment, Now.AddHours(-ageHours));
        _repo.Payments.Add(payment);
        return payment;
    }

    private static void SetUpdatedAt(PaymentEntity payment, DateTime? value) =>
        typeof(PaymentEntity).GetProperty(nameof(PaymentEntity.UpdatedAt))!.SetValue(payment, value);

    [Fact]
    public async Task A_refund_the_provider_confirms_succeeded_is_dispatched_for_finalization()
    {
        StuckPayment("rf-1");
        _gateway.GetRefundAsync("rf-1", Arg.Any<CancellationToken>())
            .Returns(new GatewayRefund("rf-1", "pr-1", GatewayRefundStatus.Succeeded, "SUCCEEDED", 500m, "PHP"));

        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        Assert.Equal(1, resolved);
        await _mediator.Received(1).Send(
            Arg.Is<ProcessRefundWebhookCommand>(c =>
                c.Provider == PaymentProviders.Xendit && c.ProviderRefundId == "rf-1" && c.Status == "SUCCEEDED"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A still-Pending poll is dispatched through the same path as any other status - the real
    /// ProcessRefundWebhookHandler treats an unrecognised/non-terminal status as a harmless no-op
    /// (Result.Ok, nothing changed), so this only asserts the resolver doesn't special-case it or
    /// error on it.
    /// </summary>
    [Fact]
    public async Task A_still_pending_poll_is_dispatched_without_erroring()
    {
        StuckPayment("rf-1");
        _gateway.GetRefundAsync("rf-1", Arg.Any<CancellationToken>())
            .Returns(new GatewayRefund("rf-1", "pr-1", GatewayRefundStatus.Pending, "PENDING", 500m, "PHP"));

        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        await _mediator.Received(1).Send(Arg.Any<ProcessRefundWebhookCommand>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task No_provider_record_of_the_refund_is_left_alone_without_dispatching()
    {
        StuckPayment("rf-1");
        _gateway.GetRefundAsync("rf-1", Arg.Any<CancellationToken>()).Returns((GatewayRefund?)null);

        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        Assert.Equal(0, resolved);
        await _mediator.DidNotReceive().Send(Arg.Any<ProcessRefundWebhookCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_payment_with_no_provider_refund_id_is_skipped_without_calling_the_gateway()
    {
        var payment = StuckPayment("rf-1");
        // Simulate a reservation that never reached the provider - shouldn't occur given
        // ReleaseReservationAsync, but defend against it rather than guess at a provider call.
        typeof(PaymentEntity).GetProperty(nameof(PaymentEntity.ProviderRefundId))!.SetValue(payment, null);

        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        Assert.Equal(0, resolved);
        await _gateway.DidNotReceive().GetRefundAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_unreachable_provider_does_not_abandon_the_rest_of_the_batch()
    {
        StuckPayment("rf-bad");
        StuckPayment("rf-good");
        _gateway.GetRefundAsync("rf-bad", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GatewayRefund?>(new HttpRequestException("provider unreachable")));
        _gateway.GetRefundAsync("rf-good", Arg.Any<CancellationToken>())
            .Returns(new GatewayRefund("rf-good", "pr-1", GatewayRefundStatus.Succeeded, "SUCCEEDED", 500m, "PHP"));

        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task Routes_to_the_gateway_that_collected_the_payment()
    {
        StuckPayment("rf-1");
        _gateway.GetRefundAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GatewayRefund("rf-1", "pr-1", GatewayRefundStatus.Succeeded, "SUCCEEDED", 500m, "PHP"));

        await Resolver().ResolveAsync(Now.AddHours(-6));

        _factory.Received(1).Get(PaymentProviders.Xendit);
        _factory.DidNotReceive().GetActive();
    }

    [Fact]
    public async Task Nothing_stuck_does_not_touch_the_gateway()
    {
        var resolved = await Resolver().ResolveAsync(Now.AddHours(-6));

        Assert.Equal(0, resolved);
        await _gateway.DidNotReceive().GetRefundAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
