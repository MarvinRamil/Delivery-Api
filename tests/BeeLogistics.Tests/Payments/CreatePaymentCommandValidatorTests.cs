using BeeLogistics.Modules.Payment.Application.DTOs;
using BeeLogistics.Modules.Payment.Application.Handlers;
using BeeLogistics.Modules.Payment.Application.Validators;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// POST /api/payments had no validator at all, so the amount, currency and method went from the
/// request body to a real gateway checkout unchecked — and an unparseable Method threw out of
/// the handler as a 500 rather than a 400.
/// </summary>
public class CreatePaymentCommandValidatorTests
{
    private readonly CreatePaymentCommandValidator _validator = new();

    private static CreatePaymentCommand Command(
        decimal amount = 500m,
        string method = "EWallet",
        string currency = "PHP",
        string payerEmail = "payer@example.com",
        Guid? customerId = null)
        => new(
            new CreatePaymentDto(
                BookingId: null,
                CustomerId: customerId ?? Guid.NewGuid(),
                Amount: amount,
                PayerEmail: payerEmail,
                Description: "Delivery",
                Method: method,
                Currency: currency),
            CallerUserId: Guid.NewGuid(),
            IsElevated: false);

    [Fact]
    public void Accepts_a_well_formed_command()
        => Assert.True(_validator.Validate(Command()).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public void Rejects_non_positive_amounts(decimal amount)
        => Assert.False(_validator.Validate(Command(amount: amount)).IsValid);

    [Fact]
    public void Rejects_amounts_above_the_ceiling()
        => Assert.False(_validator.Validate(Command(amount: CreatePaymentCommandValidator.MaxAmount + 1)).IsValid);

    [Fact]
    public void Accepts_an_amount_exactly_at_the_ceiling()
        => Assert.True(_validator.Validate(Command(amount: CreatePaymentCommandValidator.MaxAmount)).IsValid);

    /// <summary>
    /// PayMongo converts to centavos, so a third decimal place would be silently rounded and the
    /// customer charged something other than the stored amount.
    /// </summary>
    [Fact]
    public void Rejects_sub_centavo_amounts()
        => Assert.False(_validator.Validate(Command(amount: 100.005m)).IsValid);

    [Fact]
    public void Rejects_non_php_currency()
        => Assert.False(_validator.Validate(Command(currency: "USD")).IsValid);

    [Fact]
    public void Accepts_lowercase_php()
        => Assert.True(_validator.Validate(Command(currency: "php")).IsValid);

    [Fact]
    public void Rejects_an_unparseable_method()
        => Assert.False(_validator.Validate(Command(method: "NotAMethod")).IsValid);

    /// <summary>
    /// Cash payments are created server-side by the booking flow with the fare derived from the
    /// booking; routing one through this endpoint would send a cash payment to a card gateway.
    /// </summary>
    [Fact]
    public void Rejects_cash_through_the_online_endpoint()
        => Assert.False(_validator.Validate(Command(method: "Cash")).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Rejects_a_malformed_payer_email(string email)
        => Assert.False(_validator.Validate(Command(payerEmail: email)).IsValid);

    [Fact]
    public void Rejects_an_empty_customer_id()
        => Assert.False(_validator.Validate(Command(customerId: Guid.Empty)).IsValid);
}
