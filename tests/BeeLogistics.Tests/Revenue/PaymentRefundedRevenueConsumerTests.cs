using BeeLogistics.Modules.Revenue.Application.Consumers;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Revenue;

/// <summary>
/// A refund used to leave PlatformCommission standing as Recorded forever, so a driver's earnings
/// history kept showing the commission/net of a booking that no longer stood. This flips the
/// record to Reversed instead of deleting it, keeping the audit trail intact.
/// </summary>
public class PaymentRefundedRevenueConsumerTests
{
    private readonly IPlatformCommissionRepository _repository = Substitute.For<IPlatformCommissionRepository>();

    private PaymentRefundedRevenueConsumer Consumer() =>
        new(_repository, NullLogger<PaymentRefundedRevenueConsumer>.Instance);

    private static ConsumeContext<PaymentRefundedEvent> ContextFor(PaymentRefundedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<PaymentRefundedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    [Fact]
    public async Task A_refund_marks_the_matching_commission_reversed()
    {
        var bookingId = Guid.NewGuid();
        var commission = new PlatformCommission(bookingId, Guid.NewGuid(), "Cashless", 500m, 0.05m);
        _repository.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(commission);
        var msg = new PaymentRefundedEvent { BookingId = bookingId, DriverId = Guid.NewGuid(), AmountRefunded = 500m };

        await Consumer().Consume(ContextFor(msg));

        Assert.Equal(PlatformCommissionStatus.Reversed, commission.Status);
        await _repository.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_is_a_no_op()
    {
        var bookingId = Guid.NewGuid();
        var commission = new PlatformCommission(bookingId, Guid.NewGuid(), "Cashless", 500m, 0.05m);
        commission.MarkReversed();
        _repository.GetByBookingIdAsync(bookingId, Arg.Any<CancellationToken>()).Returns(commission);
        var msg = new PaymentRefundedEvent { BookingId = bookingId, DriverId = Guid.NewGuid(), AmountRefunded = 500m };

        await Consumer().Consume(ContextFor(msg));

        Assert.Equal(PlatformCommissionStatus.Reversed, commission.Status);
    }

    [Fact]
    public async Task No_matching_commission_does_not_throw()
    {
        var msg = new PaymentRefundedEvent { BookingId = Guid.NewGuid(), DriverId = Guid.NewGuid(), AmountRefunded = 200m };

        await Consumer().Consume(ContextFor(msg));

        await _repository.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
