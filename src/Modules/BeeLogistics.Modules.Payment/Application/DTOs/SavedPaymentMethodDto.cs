namespace BeeLogistics.Modules.Payment.Application.DTOs;

/// <summary>
/// DTO for saved payment method.
/// SECURITY: Never includes full card numbers - only last 4 digits.
/// </summary>
public record SavedPaymentMethodDto(
    Guid Id,
    Guid CustomerId,
    PaymentMethodTypeDto Type,
    string Last4Digits,
    string? CardBrand,
    int? ExpiryMonth,
    int? ExpiryYear,
    string? CardholderName,
    bool IsDefault,
    bool IsActive,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// DTO for creating a saved payment method.
/// SECURITY: Requires Xendit payment method token (card is tokenized by Xendit, not stored).
/// </summary>
public record CreateSavedPaymentMethodDto(
    string XenditPaymentMethodId, // Xendit payment method token from tokenization
    PaymentMethodTypeDto Type,
    string Last4Digits,
    string? CardBrand = null,
    int? ExpiryMonth = null,
    int? ExpiryYear = null,
    string? CardholderName = null,
    bool IsDefault = false
);

/// <summary>
/// DTO for updating a saved payment method.
/// SECURITY: Cannot update Xendit tokens or last4Digits (immutable for security).
/// </summary>
public record UpdateSavedPaymentMethodDto(
    string? CardholderName = null,
    int? ExpiryMonth = null,
    int? ExpiryYear = null,
    bool? IsDefault = null
);

/// <summary>
/// Payment method type enum (matches domain enum).
/// </summary>
public enum PaymentMethodTypeDto
{
    CreditCard,
    DebitCard,
    EWallet
}
