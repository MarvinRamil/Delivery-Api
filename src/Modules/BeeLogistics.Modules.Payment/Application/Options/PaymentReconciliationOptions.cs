using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Application;

/// <summary>
/// Tuning for the hourly reconciliation job.
///
/// These are configurable where the job's detection windows are hardcoded constants, because
/// expiring a checkout is the one thing the job does that <b>changes state at the provider</b>. If
/// it ever misbehaves, ops needs to widen the window or switch it off without waiting for a deploy.
/// </summary>
public class PaymentReconciliationOptions
{
    public const string SectionName = "Payment:Reconciliation";

    /// <summary>
    /// Whether the job expires abandoned provider checkouts. On by default - shipping it off would
    /// reproduce the dead-code problem this exists to fix - but present as a kill switch.
    /// </summary>
    public bool ExpireAbandonedCheckouts { get; set; } = true;

    /// <summary>
    /// How long a payment must sit untouched in Pending before its checkout is considered abandoned.
    ///
    /// Generous on purpose. Expiring a checkout a customer is midway through paying is far worse
    /// than leaving a dead one open for a few extra hours: they would land on a broken page with
    /// money possibly already committed at their bank. Twelve hours is well beyond any plausible
    /// single checkout - including bank OTP and e-wallet redirects - while still well short of
    /// leaving a payable link alive for a day.
    /// </summary>
    public int AbandonedCheckoutGraceHours { get; set; } = 12;

    /// <summary>
    /// Ceiling on provider expire calls per run, so a backlog cannot turn one job execution into
    /// thousands of outbound requests. The remainder is picked up on the next hourly run.
    /// </summary>
    public int MaxCheckoutsExpiredPerRun { get; set; } = 200;

    /// <summary>
    /// How far back the sweep looks for bookings cancelled with an unrefunded online payment (#51).
    ///
    /// Default 168 hours = 7 days, matching the refund window: past that a refund needs
    /// BypassTimeLimit and a human anyway, so counting it hourly forever would be a permanently
    /// non-zero alert nobody can clear.
    /// </summary>
    public int CancelledBookingAuditLookbackHours { get; set; } = 168;

    /// <summary>
    /// Ceiling on cancelled bookings examined per run, matching <see cref="MaxCheckoutsExpiredPerRun"/>.
    /// Unlike that one this costs no provider calls, only a bounded query, so it can be larger.
    /// </summary>
    public int MaxCancelledBookingsAuditedPerRun { get; set; } = 500;
}

/// <summary>
/// Validates <see cref="PaymentReconciliationOptions"/> at startup (fail fast), matching
/// <see cref="PaymentRefundOptionsValidator"/>.
/// </summary>
public class PaymentReconciliationOptionsValidator : IValidateOptions<PaymentReconciliationOptions>
{
    /// <summary>
    /// Below this, the window stops being a safety margin. A checkout an hour old can still be one
    /// the customer is actively working through, so a misconfiguration here would cancel live
    /// payments rather than dead ones.
    /// </summary>
    private const int MinimumSafeGraceHours = 2;

    public ValidateOptionsResult Validate(string? name, PaymentReconciliationOptions options)
    {
        if (options.ExpireAbandonedCheckouts && options.AbandonedCheckoutGraceHours < MinimumSafeGraceHours)
            return ValidateOptionsResult.Fail(
                $"{PaymentReconciliationOptions.SectionName}:{nameof(PaymentReconciliationOptions.AbandonedCheckoutGraceHours)} " +
                $"must be at least {MinimumSafeGraceHours} - a shorter window risks expiring checkouts customers are still paying. " +
                $"To disable expiry entirely, set {nameof(PaymentReconciliationOptions.ExpireAbandonedCheckouts)} to false.");

        if (options.MaxCheckoutsExpiredPerRun < 1)
            return ValidateOptionsResult.Fail(
                $"{PaymentReconciliationOptions.SectionName}:{nameof(PaymentReconciliationOptions.MaxCheckoutsExpiredPerRun)} must be >= 1.");

        if (options.CancelledBookingAuditLookbackHours < 1)
            return ValidateOptionsResult.Fail(
                $"{PaymentReconciliationOptions.SectionName}:{nameof(PaymentReconciliationOptions.CancelledBookingAuditLookbackHours)} must be >= 1 " +
                $"- a zero or negative lookback would silently audit nothing.");

        if (options.MaxCancelledBookingsAuditedPerRun < 1)
            return ValidateOptionsResult.Fail(
                $"{PaymentReconciliationOptions.SectionName}:{nameof(PaymentReconciliationOptions.MaxCancelledBookingsAuditedPerRun)} must be >= 1.");

        return ValidateOptionsResult.Success;
    }
}
