using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Services;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Creation idempotency (GitLab #30).
///
/// Two defects: the payment row was persisted only *after* the provider call, so a crash in
/// between left a payable checkout with no local record; and the dedupe keyed on the provider id
/// returned by that same call, so it could never catch a client double-submit — every call minted
/// a fresh invoice id and PaymentNumber.
/// </summary>
public class CreatePaymentIdempotencyTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();
    private readonly IBookingOwnershipVerifier _bookingOwnership = Substitute.For<IBookingOwnershipVerifier>();

    private static readonly Guid Customer = Guid.NewGuid();
    private int _checkoutCalls;

    public CreatePaymentIdempotencyTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.PayMongo);
        _factory.GetActive().Returns(_gateway);
        _gateway.CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _checkoutCalls++;
                return new CheckoutSession(
                    ProviderPaymentId: $"cs_{_checkoutCalls}",
                    ReferenceId: ci.Arg<CreateCheckoutRequest>().ReferenceId,
                    Status: GatewayPaymentStatus.Pending,
                    RawStatus: "pending",
                    Amount: ci.Arg<CreateCheckoutRequest>().Amount,
                    CheckoutUrl: $"https://checkout/{_checkoutCalls}",
                    PaidAt: null);
            });
    }

    private CreatePaymentHandler Handler() => new(
        _repo, _factory, new PaymentAccessPolicy(_bookingOwnership), NullLogger<CreatePaymentHandler>.Instance);

    private static CreatePaymentDto Dto() =>
        new(BookingId: null, CustomerId: Customer, Amount: 500m,
            PayerEmail: "payer@example.com", Description: "Delivery", Method: "EWallet");

    private Task<BeeLogistics.Shared.Abstractions.Result<PaymentDto>> Create(string? key) =>
        Handler().Handle(new CreatePaymentCommand(Dto(), Customer, IsElevated: false, IdempotencyKey: key), CancellationToken.None);

    [Fact]
    public async Task Repeated_key_returns_the_same_payment_and_opens_only_one_checkout()
    {
        var first = await Create("key-1");
        var second = await Create("key-1");

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Single(_repo.Payments);
        Assert.Equal(1, _checkoutCalls);
    }

    [Fact]
    public async Task Different_keys_create_separate_payments()
    {
        await Create("key-1");
        await Create("key-2");

        Assert.Equal(2, _repo.Payments.Count);
        Assert.Equal(2, _checkoutCalls);
    }

    /// <summary>
    /// Absent key preserves the previous behaviour, so existing clients are unaffected.
    /// </summary>
    [Fact]
    public async Task Without_a_key_each_call_creates_a_payment()
    {
        await Create(null);
        await Create(null);

        Assert.Equal(2, _repo.Payments.Count);
    }

    /// <summary>
    /// The payment must be persisted before the provider is called, so a crash in between leaves
    /// a record of the checkout rather than an orphaned payable one at the provider.
    /// </summary>
    [Fact]
    public async Task Payment_is_persisted_even_when_the_gateway_call_fails()
    {
        _gateway.CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns<CheckoutSession>(_ => throw new HttpRequestException("provider down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Create("key-1"));

        Assert.Single(_repo.Payments);
        Assert.Equal(PaymentStatus.Pending, _repo.Payments[0].Status);
        Assert.Null(_repo.Payments[0].ProviderCheckoutUrl);
    }

    /// <summary>
    /// Retrying after that failure must finish the existing row rather than starting a second
    /// payment — otherwise the orphan is replaced by two orphans.
    /// </summary>
    [Fact]
    public async Task Retry_after_a_gateway_failure_completes_the_existing_payment()
    {
        _gateway.CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns<CheckoutSession>(_ => throw new HttpRequestException("provider down"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create("key-1"));
        var orphanId = _repo.Payments[0].Id;

        // Provider recovers.
        _gateway.CreateCheckoutAsync(Arg.Any<CreateCheckoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSession("cs_ok", "ref", GatewayPaymentStatus.Pending, "pending", 500m, "https://checkout/ok", null));

        var retried = await Create("key-1");

        Assert.True(retried.IsSuccess);
        Assert.Single(_repo.Payments);
        Assert.Equal(orphanId, retried.Value!.Id);
        Assert.Equal("https://checkout/ok", _repo.Payments[0].ProviderCheckoutUrl);
    }

    /// <summary>
    /// The key is scoped per customer by a unique index, so one customer's key must not resolve
    /// to another customer's payment.
    /// </summary>
    [Fact]
    public async Task Keys_are_scoped_per_customer()
    {
        var other = Guid.NewGuid();
        await Create("shared-key");

        var otherResult = await Handler().Handle(
            new CreatePaymentCommand(
                new CreatePaymentDto(null, other, 500m, "b@example.com", "Delivery", "EWallet"),
                other, IsElevated: false, IdempotencyKey: "shared-key"),
            CancellationToken.None);

        Assert.True(otherResult.IsSuccess);
        Assert.Equal(2, _repo.Payments.Count);
    }
}
