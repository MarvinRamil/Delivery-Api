using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Payment = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// GitLab #51: the standing-backlog half of cancelled-booking reconciliation.
///
/// BookingCancelledPaymentConsumer reacts to each cancellation as it happens, but events get lost —
/// the outbox can fail to flush, a consumer can land in an error queue. A booking whose payment is
/// never reconciled is precisely the silent failure this issue exists to stop, and the thing going
/// missing is a customer's money, so the sweep re-counts the backlog hourly rather than trusting
/// the live path alone.
///
/// It is detection only: it must never move money, whatever the refund switch says.
/// </summary>
public class CancelledBookingAuditTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakePaymentRepository _repo = new();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    private CancelledBookingPaymentAuditor Auditor(int lookbackHours = 168, int maxPerRun = 500) => new(
        _repo,
        _mediator,
        Options.Create(new PaymentReconciliationOptions
        {
            CancelledBookingAuditLookbackHours = lookbackHours,
            MaxCancelledBookingsAuditedPerRun = maxPerRun
        }),
        NullLogger<CancelledBookingPaymentAuditor>.Instance);

    private void BookingsModuleReturns(params Guid[] bookingIds)
        => _mediator.Send(Arg.Any<GetCancelledBookingsSinceQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok<IReadOnlyList<CancelledBookingInfo>>(
                bookingIds.Select(id => new CancelledBookingInfo(id, null, null, Now, "No driver found")).ToList()));

    private Payment PaidPayment(Guid bookingId, decimal amount = 500m)
    {
        var payment = Payment.Create(bookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.SetProviderCheckout(PaymentProviders.Xendit, "inv-1", "https://invoice.url", payment.PaymentNumber);
        payment.MarkAsPaid(Now);
        _repo.Payments.Add(payment);
        return payment;
    }

    [Fact]
    public async Task A_cancelled_booking_whose_online_payment_is_still_paid_is_counted()
    {
        var bookingId = Guid.NewGuid();
        BookingsModuleReturns(bookingId);
        PaidPayment(bookingId);

        Assert.Equal(1, await Auditor().AuditAsync(Now));
    }

    /// <summary>Detection only. A sweep that mutated would be a second, unreviewed refund path.</summary>
    [Fact]
    public async Task The_sweep_never_changes_a_payment()
    {
        var bookingId = Guid.NewGuid();
        BookingsModuleReturns(bookingId);
        var payment = PaidPayment(bookingId);

        await Auditor().AuditAsync(Now);

        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(0, _repo.SaveCount);
    }

    /// <summary>
    /// Refunded and RefundPending are resolved or in flight; Cash settled off-platform; Pending was
    /// never collected. None of them is money owed back, so counting them would make the alert
    /// permanently non-zero and therefore useless.
    /// </summary>
    [Fact]
    public async Task Payments_that_are_not_owed_back_are_not_counted()
    {
        var refunded = Guid.NewGuid();
        var refundPending = Guid.NewGuid();
        var cash = Guid.NewGuid();
        var neverPaid = Guid.NewGuid();
        BookingsModuleReturns(refunded, refundPending, cash, neverPaid);

        PaidPayment(refunded).MarkAsRefunded(Now, "rf-1", 500m);
        PaidPayment(refundPending).MarkAsRefundPending("rf-2", Guid.NewGuid(), 500m);
        _repo.Payments.Add(Payment.CreateCashOnDelivery(cash, Guid.NewGuid(), 500m));
        _repo.Payments.Add(Payment.Create(neverPaid, Guid.NewGuid(), 500m, PaymentMethod.EWallet));

        Assert.Equal(0, await Auditor().AuditAsync(Now));
    }

    [Fact]
    public async Task A_cancelled_booking_with_no_payment_at_all_is_not_counted()
    {
        BookingsModuleReturns(Guid.NewGuid());

        Assert.Equal(0, await Auditor().AuditAsync(Now));
    }

    [Fact]
    public async Task No_cancelled_bookings_is_a_clean_zero()
    {
        BookingsModuleReturns();

        Assert.Equal(0, await Auditor().AuditAsync(Now));
    }

    /// <summary>The lookback and the cap are the Bookings module's to apply, so pass them through intact.</summary>
    [Fact]
    public async Task The_lookback_window_and_cap_are_passed_to_the_bookings_module()
    {
        BookingsModuleReturns();

        await Auditor(lookbackHours: 72, maxPerRun: 250).AuditAsync(Now);

        await _mediator.Received(1).Send(
            Arg.Is<GetCancelledBookingsSinceQuery>(q =>
                q.SinceUtc == Now.AddHours(-72) && q.Limit == 250),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The sweep shares an hourly job with three other checks. If Bookings cannot answer, it must
    /// report zero and let the rest of the job run, not take reconciliation down with it.
    /// </summary>
    [Fact]
    public async Task A_failed_cross_module_query_degrades_to_zero()
    {
        _mediator.Send(Arg.Any<GetCancelledBookingsSinceQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<IReadOnlyList<CancelledBookingInfo>>("Bookings unavailable"));

        Assert.Equal(0, await Auditor().AuditAsync(Now));
    }
}
