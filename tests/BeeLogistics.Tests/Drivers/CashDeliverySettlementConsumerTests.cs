using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Consumers;
using BeeLogistics.Modules.Drivers.Application.Handlers;
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

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// A failed cash settlement debit used to be swallowed: LogWarning and return, so MassTransit's
/// retry/redelivery policy never engaged. This asserts the failure now rethrows instead - safe
/// only because ApplyCashSettlementDebitCommandHandler is booking-id idempotent and atomic (see
/// WalletDebitIdempotencyTests), so a retried delivery cannot double-debit.
/// </summary>
public class CashDeliverySettlementConsumerTests
{
    private readonly FakePaymentRepository _payments = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    private CashDeliverySettlementConsumer Consumer(decimal chargeRate = 0.05m) => new(
        _payments,
        _mediator,
        Options.Create(new DriverWalletOptions { CashDeliveryPlatformChargeRateOverride = chargeRate }),
        NullLogger<CashDeliverySettlementConsumer>.Instance);

    private static ConsumeContext<BookingCompletedEvent> ContextFor(BookingCompletedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<BookingCompletedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task A_failed_debit_rethrows_instead_of_being_silently_dropped()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        _payments.Add(Payment.CreateCashOnDelivery(bookingId, Guid.NewGuid(), 500m));

        _mediator.Send(Arg.Any<ApplyCashSettlementDebitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail("Top-up wallet below allowed negative limit"));

        var msg = new BookingCompletedEvent
        {
            BookingId = bookingId,
            DriverId = driverId,
            CustomerId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Consumer().Consume(ContextFor(msg)));
    }

    [Fact]
    public async Task A_successful_debit_does_not_throw()
    {
        var bookingId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        _payments.Add(Payment.CreateCashOnDelivery(bookingId, Guid.NewGuid(), 500m));

        _mediator.Send(Arg.Any<ApplyCashSettlementDebitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var msg = new BookingCompletedEvent
        {
            BookingId = bookingId,
            DriverId = driverId,
            CustomerId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow,
        };

        await Consumer().Consume(ContextFor(msg));

        await _mediator.Received(1).Send(
            Arg.Is<ApplyCashSettlementDebitCommand>(c => c.BookingId == bookingId && c.DriverId == driverId && c.Amount == 25m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cashless_booking_never_reaches_the_mediator()
    {
        var bookingId = Guid.NewGuid();
        var payment = Payment.Create(bookingId, Guid.NewGuid(), 500m, PaymentMethod.CreditCard);
        _payments.Add(payment);

        var msg = new BookingCompletedEvent
        {
            BookingId = bookingId,
            DriverId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            CompletedAt = DateTime.UtcNow,
        };

        await Consumer().Consume(ContextFor(msg));

        await _mediator.DidNotReceive().Send(Arg.Any<ApplyCashSettlementDebitCommand>(), Arg.Any<CancellationToken>());
    }
}
