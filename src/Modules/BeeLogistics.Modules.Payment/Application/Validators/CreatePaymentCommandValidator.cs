using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Domain;
using FluentValidation;

namespace BeeLogistics.Modules.Payment.Application.Validators;

/// <summary>
/// Validates CreatePaymentCommand. Until this existed the endpoint had no validator at all, so
/// the amount, currency and method arrived from the request body and went straight to a real
/// gateway checkout — an unparseable Method threw out of the handler as a 500, and any amount
/// was accepted.
///
/// This does not decide *who* may create the payment (see IPaymentAccessPolicy) nor whether the
/// amount matches the booking fare — it only rejects input that is malformed or nonsensical
/// before it reaches the provider.
/// </summary>
public class CreatePaymentCommandValidator : AbstractValidator<CreatePaymentCommand>
{
    /// <summary>
    /// Upper sanity bound, not a business rule: it exists so a typo or a tampered client cannot
    /// open a multi-million-peso checkout at the provider. Raise it if real fares ever approach it.
    /// </summary>
    public const decimal MaxAmount = 1_000_000m;

    public const int MaxDescriptionLength = 500;
    public const int MaxPayerEmailLength = 254;

    public CreatePaymentCommandValidator()
    {
        RuleFor(x => x.Dto.CustomerId)
            .NotEmpty()
            .WithMessage("Customer ID is required.");

        RuleFor(x => x.Dto.Amount)
            .GreaterThan(0)
            .WithMessage("Amount must be greater than zero.")
            .LessThanOrEqualTo(MaxAmount)
            .WithMessage($"Amount must not exceed {MaxAmount:N0}.")
            .Must(HaveAtMostTwoDecimals)
            .WithMessage("Amount must not have more than two decimal places.");

        // Both gateways are configured for PHP only, and the column is 3 chars.
        RuleFor(x => x.Dto.Currency)
            .NotEmpty()
            .Must(c => string.Equals(c, "PHP", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Currency must be PHP.");

        // The handler calls Enum.Parse on this; without the guard a bad value is a 500.
        RuleFor(x => x.Dto.Method)
            .NotEmpty()
            .Must(BeAParseableMethod)
            .WithMessage($"Method must be one of: {string.Join(", ", Enum.GetNames<PaymentMethod>())}.");

        // Kept as its own rule on purpose: a trailing .When() applies to every validator in the
        // chain it terminates, so folding this into the rule above would make an unparseable
        // Method skip the parseability check as well and pass validation.
        RuleFor(x => x.Dto.Method)
            .Must(NotBeCash)
            .When(x => BeAParseableMethod(x.Dto.Method))
            .WithMessage("Cash payments are recorded by the booking flow, not through this endpoint.");

        // Forwarded to the provider as the payer email on the checkout.
        RuleFor(x => x.Dto.PayerEmail)
            .NotEmpty()
            .WithMessage("Payer email is required.")
            .MaximumLength(MaxPayerEmailLength)
            .EmailAddress()
            .WithMessage("Payer email must be a valid email address.");

        RuleFor(x => x.Dto.Description)
            .MaximumLength(MaxDescriptionLength)
            .WithMessage($"Description must not exceed {MaxDescriptionLength} characters.");
    }

    private static bool HaveAtMostTwoDecimals(decimal amount)
        => decimal.Round(amount, 2) == amount;

    private static bool BeAParseableMethod(string? method)
        => !string.IsNullOrWhiteSpace(method) && Enum.TryParse<PaymentMethod>(method, true, out _);

    private static bool NotBeCash(string? method)
        => !Enum.TryParse<PaymentMethod>(method, true, out var parsed) || parsed != PaymentMethod.Cash;
}
