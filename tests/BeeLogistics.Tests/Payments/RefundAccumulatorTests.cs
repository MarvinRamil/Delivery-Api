using BeeLogistics.Modules.Payment.Domain;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// The refund accumulator (GitLab #34).
///
/// The cap used to be measured against the full payment amount, which held only incidentally: the
/// status machine short-circuits Refunded and RefundPending, so a second partial refund was blocked
/// by the state rather than by the cap. TotalRefunded makes the invariant stand on its own, so it
/// survives the first genuine partial-refund feature.
///
/// The rule these tests defend is that the total is a sum of amounts *actually moved* - never a
/// freshly recomputed share, which is where repeated partial operations drift (see #28).
/// </summary>
public class RefundAccumulatorTests
{
    private static PaymentEntity PaidPayment(decimal amount = 500m)
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.MarkAsPaid(DateTime.UtcNow);
        return payment;
    }

    [Fact]
    public void A_fresh_payment_is_fully_refundable()
    {
        var payment = PaidPayment();

        Assert.Equal(0m, payment.TotalRefunded);
        Assert.Equal(500m, payment.RefundableAmount);
    }

    [Fact]
    public void A_confirmed_refund_reduces_the_refundable_balance()
    {
        var payment = PaidPayment();

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 200m);

        Assert.Equal(200m, payment.TotalRefunded);
        Assert.Equal(300m, payment.RefundableAmount);
    }

    /// <summary>
    /// A refund accepted by the provider but not yet confirmed has moved no money, so it must not
    /// consume refundable headroom - otherwise a refund that later fails would permanently shrink
    /// what can be returned.
    /// </summary>
    [Fact]
    public void A_pending_refund_does_not_consume_headroom()
    {
        var payment = PaidPayment();

        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);

        Assert.Equal(0m, payment.TotalRefunded);
        Assert.Equal(500m, payment.RefundableAmount);
    }

    [Fact]
    public void A_failed_refund_restores_the_full_balance()
    {
        var payment = PaidPayment();
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);

        payment.MarkRefundFailed("INSUFFICIENT_BALANCE");

        Assert.Equal(0m, payment.TotalRefunded);
        Assert.Equal(500m, payment.RefundableAmount);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    /// <summary>
    /// The in-flight context must survive a failure (GitLab #38).
    ///
    /// #34 cleared both fields here, which broke the late-SUCCEEDED path: provider webhooks are
    /// redelivered and unordered, so a FAILED arriving before a SUCCEEDED for the same refund id is
    /// a delivery artefact. With the context gone, the handler had no driver to debit and fell back
    /// to the full payment amount - the driver kept money that had been returned to the customer.
    /// </summary>
    [Fact]
    public void A_failed_refund_keeps_the_in_flight_amount_and_driver()
    {
        var payment = PaidPayment();
        var driverId = Guid.NewGuid();
        payment.MarkAsRefundPending("rf-1", driverId, 200m);

        payment.MarkRefundFailed("INSUFFICIENT_BALANCE");

        Assert.Equal(200m, payment.RefundAmount);
        Assert.Equal(driverId, payment.RefundDriverId);
        Assert.Equal("rf-1", payment.ProviderRefundId);
    }

    /// <summary>
    /// Starting a second refund is what actually resets the context, which is why clearing it on
    /// failure was unnecessary in the first place.
    /// </summary>
    [Fact]
    public void Starting_another_refund_replaces_the_in_flight_context()
    {
        var payment = PaidPayment();
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);
        payment.MarkRefundFailed("INSUFFICIENT_BALANCE");

        var secondDriver = Guid.NewGuid();
        payment.MarkAsRefundPending("rf-2", secondDriver, 300m);

        Assert.Equal(300m, payment.RefundAmount);
        Assert.Equal(secondDriver, payment.RefundDriverId);
        Assert.Equal("rf-2", payment.ProviderRefundId);
    }

    /// <summary>
    /// Load-bearing, not cosmetic: the refund handler re-applies MarkAsRefunded after an xmin
    /// concurrency conflict, and refund webhooks are redelivered. Accumulating twice would silently
    /// shrink the refundable balance by the amount of one extra refund.
    /// </summary>
    [Fact]
    public void Marking_refunded_twice_does_not_double_count()
    {
        var payment = PaidPayment();

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 200m);
        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 200m);

        Assert.Equal(200m, payment.TotalRefunded);
        Assert.Equal(300m, payment.RefundableAmount);
    }

    /// <summary>
    /// The synchronous full-refund path never passes through RefundPending, so there is no
    /// in-flight amount to fall back on and the whole payment is the right default.
    /// </summary>
    [Fact]
    public void Defaults_to_the_full_amount_when_no_refund_was_in_flight()
    {
        var payment = PaidPayment();

        payment.MarkAsRefunded();

        Assert.Equal(500m, payment.TotalRefunded);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    [Fact]
    public void Defaults_to_the_in_flight_amount_when_one_was_pending()
    {
        var payment = PaidPayment();
        payment.MarkAsRefundPending("rf-1", Guid.NewGuid(), 200m);

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1");

        Assert.Equal(200m, payment.TotalRefunded);
        Assert.Equal(300m, payment.RefundableAmount);
    }

    /// <summary>
    /// The clamp is a last line of defence behind the handler's cap check. Reaching it means
    /// something upstream let a bad amount through; the stored total still must not claim more was
    /// returned than was ever collected.
    /// </summary>
    [Fact]
    public void Never_records_more_refunded_than_was_collected()
    {
        var payment = PaidPayment();

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 900m);

        Assert.Equal(500m, payment.TotalRefunded);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    [Fact]
    public void Refundable_amount_never_goes_negative()
    {
        var payment = PaidPayment(0.01m);

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 0.01m);

        Assert.Equal(0m, payment.RefundableAmount);
    }

    /// <summary>
    /// The constraint from #28: partial refunds must sum to the original to the centavo. These are
    /// the fares where a recomputed share would drift - 100.10 split three ways, and 33.33 split
    /// into thirds - so the total has to come from amounts actually moved.
    /// </summary>
    [Theory]
    [InlineData(100.10, 33.37, 33.37, 33.36)]
    [InlineData(33.33, 11.11, 11.11, 11.11)]
    [InlineData(0.03, 0.01, 0.01, 0.01)]
    public void Sequential_partial_refunds_sum_exactly_to_the_original(
        decimal amount, decimal first, decimal second, decimal third)
    {
        // Each leg is applied as its own confirmed refund, resetting to Paid in between the way a
        // real partial-refund flow would.
        var payment = PaidPayment(amount);

        foreach (var leg in new[] { first, second, third })
        {
            payment.MarkAsRefunded(DateTime.UtcNow, "rf", leg);
            payment.MarkRefundFailed("reset for next leg"); // returns Status to Paid, keeps TotalRefunded
        }

        Assert.Equal(amount, payment.TotalRefunded);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    /// <summary>
    /// The headroom has to shrink leg by leg, so a final over-sized refund is refused rather than
    /// taking the total past what was collected.
    /// </summary>
    [Fact]
    public void Refundable_balance_shrinks_across_sequential_partials()
    {
        var payment = PaidPayment(100.10m);

        payment.MarkAsRefunded(DateTime.UtcNow, "rf-1", 60.05m);
        Assert.Equal(40.05m, payment.RefundableAmount);

        payment.MarkRefundFailed("reset");
        payment.MarkAsRefunded(DateTime.UtcNow, "rf-2", 40.05m);

        Assert.Equal(100.10m, payment.TotalRefunded);
        Assert.Equal(0m, payment.RefundableAmount);
    }
}
