using BeeLogistics.Modules.Payment.Application.Handlers;
using FluentValidation;

namespace BeeLogistics.Modules.Payment.Application.Validators;

/// <summary>
/// Validates RefundPaymentCommand. Keeps validation concerns separate (SRP) and ensures
/// only allowed Xendit reason codes and safe amounts are accepted (security: allowlist).
/// </summary>
public class RefundPaymentCommandValidator : AbstractValidator<RefundPaymentCommand>
{
    private static readonly HashSet<string> AllowedReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "CANCELLATION",
        "REQUESTED_BY_CUSTOMER",
        "FRAUDULENT",
        "DUPLICATE",
        "OTHERS"
    };

    public const int MaxReasonLength = 50;

    public RefundPaymentCommandValidator()
    {
        RuleFor(x => x.BookingId)
            .NotEmpty()
            .WithMessage("Booking ID is required.");

        // Null is legitimate: no driver was ever assigned, which is how a booking nobody accepted
        // reaches a refund (GitLab #51). An all-zero Guid is not - that is a caller who meant to
        // name a driver and sent nothing, and letting it through would skip the wallet reversal
        // silently on a booking a driver had actually been paid for.
        // Must, not NotEmpty: on a Guid? the latter compares against null, so an all-zero Guid
        // would sail through it.
        RuleFor(x => x.DriverId)
            .Must(id => id != Guid.Empty)
            .When(x => x.DriverId.HasValue)
            .WithMessage("Driver ID is required.");

        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .When(x => x.Amount.HasValue)
            .WithMessage("Refund amount must be greater than zero when specified.");

        RuleFor(x => x.Reason)
            .MaximumLength(MaxReasonLength)
            .When(x => !string.IsNullOrEmpty(x.Reason))
            .WithMessage($"Reason must not exceed {MaxReasonLength} characters.");

        RuleFor(x => x.Reason)
            .Must(BeAllowedReason)
            .When(x => !string.IsNullOrWhiteSpace(x.Reason))
            .WithMessage("Reason must be one of: CANCELLATION, REQUESTED_BY_CUSTOMER, FRAUDULENT, DUPLICATE, OTHERS.");
    }

    private static bool BeAllowedReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return true;
        return AllowedReasons.Contains(reason.Trim());
    }

    /// <summary>
    /// Normalizes reason to a Xendit-allowed value. Used by handler to avoid sending arbitrary strings.
    /// </summary>
    public static string NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "CANCELLATION";
        var r = reason.Trim().ToUpperInvariant();
        return AllowedReasons.Contains(r) ? r : "OTHERS";
    }
}
