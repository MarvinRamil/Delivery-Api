using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Application;

public class PaymentRefundOptions
{
    public const string SectionName = "Payment:Refund";

    /// <summary>
    /// Refund allowed only within this many hours after payment was made (PaidAt).
    /// Default 168 = 7 days. Set 0 to disable time limit (rely on BypassTimeLimit for exceptions).
    /// </summary>
    public int RefundAllowedWithinHoursAfterPayment { get; set; } = 168;

    /// <summary>
    /// Whether a booking cancelled with a linked Paid online payment is refunded automatically.
    ///
    /// Ships <b>off</b>, deliberately (#51). Two things have to be known before it can be trusted:
    /// how PayMongo actually settles a refund (synchronously, or Pending plus a later webhook), and
    /// how often this happens at all - which is what
    /// <c>bee.payments.cancelled_unrefunded</c> is there to answer. Until then the consumer counts
    /// and alerts, and refunds stay a human decision through POST /api/payments/refund.
    ///
    /// Turning it on refunds only cancellations that were the platform's fault
    /// (<c>CancelledBy == null</c>: nobody accepted the booking) and only while the payment is still
    /// inside <see cref="RefundAllowedWithinHoursAfterPayment"/>. It never sets BypassTimeLimit -
    /// see the consumer for why an aged payment must stay manual.
    /// </summary>
    public bool AutoRefundCancelledBookings { get; set; } = false;
}

/// <summary>
/// Validates PaymentRefundOptions at startup (fail fast). Keeps configuration validation separate (SRP).
/// </summary>
public class PaymentRefundOptionsValidator : IValidateOptions<PaymentRefundOptions>
{
    public ValidateOptionsResult Validate(string? name, PaymentRefundOptions options)
    {
        if (options.RefundAllowedWithinHoursAfterPayment < 0)
            return ValidateOptionsResult.Fail(
                $"{PaymentRefundOptions.SectionName}:{nameof(PaymentRefundOptions.RefundAllowedWithinHoursAfterPayment)} must be >= 0.");

        return ValidateOptionsResult.Success;
    }
}
