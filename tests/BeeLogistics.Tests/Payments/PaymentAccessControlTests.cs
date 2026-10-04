using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Object-level authorization on the payment write endpoints (GitLab #24).
///
/// Before this, POST /api/payments accepted any CustomerId from the body and
/// POST /api/payments/{id}/link-booking/{bookingId} had no check at all, so any authenticated
/// caller could attach any payment to any booking — and EarningCreditConsumer derives the
/// driver's fare from whichever payment ends up linked.
/// </summary>
public class PaymentAccessControlTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IBookingOwnershipVerifier _bookingOwnership = Substitute.For<IBookingOwnershipVerifier>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Attacker = Guid.NewGuid();
    private static readonly Guid OwnedBooking = Guid.NewGuid();
    private static readonly Guid ForeignBooking = Guid.NewGuid();

    public PaymentAccessControlTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.PayMongo);
        _factory.GetActive().Returns(_gateway);
        _gateway.CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new CheckoutSession(
                ProviderPaymentId: "cs_1",
                ReferenceId: ci.Arg<CreateCheckoutRequest>().ReferenceId,
                Status: GatewayPaymentStatus.Pending,
                RawStatus: "pending",
                Amount: ci.Arg<CreateCheckoutRequest>().Amount,
                CheckoutUrl: "https://checkout.url",
                PaidAt: null));

        // Owner owns OwnedBooking only.
        _bookingOwnership.IsOwnedByCustomerAsync(OwnedBooking, Owner, Arg.Any<CancellationToken>()).Returns(true);
        _bookingOwnership.IsOwnedByCustomerAsync(ForeignBooking, Owner, Arg.Any<CancellationToken>()).Returns(false);
        _bookingOwnership.IsOwnedByCustomerAsync(Arg.Any<Guid>(), Attacker, Arg.Any<CancellationToken>()).Returns(false);
    }

    private PaymentAccessPolicy Policy() => new(_bookingOwnership);

    private LinkPaymentToBookingHandler LinkHandler() => new(_repo, Policy());

    private CreatePaymentHandler CreateHandler() =>
        new(_repo, _factory, Policy(), NullLogger<CreatePaymentHandler>.Instance);

    private Payment UnlinkedPaymentOwnedBy(Guid customerId)
    {
        var payment = Payment.Create(null, customerId, 500m, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.PayMongo, "cs_existing", "https://checkout.url", payment.PaymentNumber);
        _repo.Payments.Add(payment);
        return payment;
    }

    private static CreatePaymentDto Dto(Guid customerId, decimal amount = 500m) =>
        new(BookingId: null, CustomerId: customerId, Amount: amount,
            PayerEmail: "payer@example.com", Description: "Delivery", Method: "EWallet");

    // --- link-booking ---

    [Fact]
    public async Task Owner_can_link_their_own_payment_to_their_own_booking()
    {
        var payment = UnlinkedPaymentOwnedBy(Owner);

        var result = await LinkHandler().Handle(
            new LinkPaymentToBookingCommand(payment.Id, OwnedBooking, Owner, IsElevated: false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OwnedBooking, payment.BookingId);
    }

    [Fact]
    public async Task Caller_cannot_link_a_payment_they_do_not_own()
    {
        var victimPayment = UnlinkedPaymentOwnedBy(Owner);

        var result = await LinkHandler().Handle(
            new LinkPaymentToBookingCommand(victimPayment.Id, ForeignBooking, Attacker, IsElevated: false), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(victimPayment.BookingId);
    }

    [Fact]
    public async Task Caller_cannot_link_their_own_payment_to_someone_elses_booking()
    {
        var payment = UnlinkedPaymentOwnedBy(Owner);

        var result = await LinkHandler().Handle(
            new LinkPaymentToBookingCommand(payment.Id, ForeignBooking, Owner, IsElevated: false), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(payment.BookingId);
    }

    /// <summary>
    /// A denial must be indistinguishable from a missing record, or the endpoint becomes an
    /// oracle for which payment ids exist.
    /// </summary>
    [Fact]
    public async Task Denied_link_is_reported_as_not_found()
    {
        var victimPayment = UnlinkedPaymentOwnedBy(Owner);
        var handler = LinkHandler();

        var denied = await handler.Handle(
            new LinkPaymentToBookingCommand(victimPayment.Id, OwnedBooking, Attacker, IsElevated: false), CancellationToken.None);
        var missing = await handler.Handle(
            new LinkPaymentToBookingCommand(Guid.NewGuid(), OwnedBooking, Attacker, IsElevated: false), CancellationToken.None);

        Assert.Equal(ResultErrorKind.NotFound, denied.ErrorKind);
        Assert.Equal(ResultErrorKind.NotFound, missing.ErrorKind);
        Assert.Equal(missing.Error, denied.Error);
    }

    [Fact]
    public async Task Backoffice_can_link_any_payment_to_any_booking()
    {
        var payment = UnlinkedPaymentOwnedBy(Owner);

        var result = await LinkHandler().Handle(
            new LinkPaymentToBookingCommand(payment.Id, ForeignBooking, Attacker, IsElevated: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ForeignBooking, payment.BookingId);
    }

    // --- create ---

    [Fact]
    public async Task Caller_cannot_create_a_payment_for_another_customer()
    {
        var result = await CreateHandler().Handle(
            new CreatePaymentCommand(Dto(Owner), Attacker, IsElevated: false), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorKind.Forbidden, result.ErrorKind);
        Assert.Empty(_repo.Payments);
    }

    /// <summary>
    /// The rejection must happen before the gateway call, or a refused request still leaves a
    /// payable checkout open at the provider.
    /// </summary>
    [Fact]
    public async Task Rejected_create_never_reaches_the_gateway()
    {
        await CreateHandler().Handle(
            new CreatePaymentCommand(Dto(Owner), Attacker, IsElevated: false), CancellationToken.None);

        await _gateway.DidNotReceive().CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_can_create_a_payment_for_themselves()
    {
        var result = await CreateHandler().Handle(
            new CreatePaymentCommand(Dto(Owner), Owner, IsElevated: false), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(_repo.Payments);
        Assert.Equal(Owner, _repo.Payments[0].CustomerId);
    }

    [Fact]
    public async Task Backoffice_can_create_a_payment_for_any_customer()
    {
        var result = await CreateHandler().Handle(
            new CreatePaymentCommand(Dto(Owner), Attacker, IsElevated: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(_repo.Payments);
    }
}
