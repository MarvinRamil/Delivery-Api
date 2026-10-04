using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Payment.Domain;

/// <summary>
/// Saved payment method for customers (cards, e-wallets).
/// SECURITY: Never stores full card numbers - only Xendit tokens and display information.
/// PCI DSS compliant - sensitive data handled by Xendit.
/// </summary>
public class SavedPaymentMethod : Entity
{
    public Guid CustomerId { get; private set; }

    /// <summary>Gateway that vaulted the token ("xendit" | "paymongo"). Get/delete route by this; charging requires it to match the active gateway.</summary>
    public string Provider { get; private set; } = "xendit";
    // Provider tokens - encrypted at rest via PaymentDbContext value converter
    public string ProviderCustomerId { get; private set; } = null!;
    public string ProviderPaymentMethodId { get; private set; } = null!;

    // TODO(provider-cleanup): legacy Xendit-named columns. Frozen (no writers) since the
    // provider-agnostic columns above replaced them; backfilled by AddPaymentProviderColumns.
    public string? XenditCustomerId { get; private set; }
    public string? XenditPaymentMethodId { get; private set; }

    // Display information only (for UI)
    public PaymentMethodType Type { get; private set; }
    public string Last4Digits { get; private set; } = null!; // Last 4 digits only
    public string? CardBrand { get; private set; } // Visa, Mastercard, etc.
    public int? ExpiryMonth { get; private set; } // 1-12
    public int? ExpiryYear { get; private set; } // YYYY format
    public string? CardholderName { get; private set; } // Optional display name
    
    // User preferences
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;
    
    // Metadata
    public DateTime? LastUsedAt { get; private set; }
    
    private SavedPaymentMethod() { } // For EF Core

    /// <summary>
    /// Create a new saved payment method.
    /// SECURITY: Validates that sensitive data is not stored directly.
    /// </summary>
    public static SavedPaymentMethod Create(
        Guid customerId,
        string provider,
        string providerCustomerId,
        string providerPaymentMethodId,
        PaymentMethodType type,
        string last4Digits,
        string? cardBrand = null,
        int? expiryMonth = null,
        int? expiryYear = null,
        string? cardholderName = null,
        bool isDefault = false)
    {
        // Validation
        if (customerId == Guid.Empty)
            throw new ArgumentException("Customer ID cannot be empty", nameof(customerId));

        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required", nameof(provider));

        if (string.IsNullOrWhiteSpace(providerCustomerId))
            throw new ArgumentException("Provider customer ID is required", nameof(providerCustomerId));

        if (string.IsNullOrWhiteSpace(providerPaymentMethodId))
            throw new ArgumentException("Provider payment method ID is required", nameof(providerPaymentMethodId));

        if (string.IsNullOrWhiteSpace(last4Digits) || last4Digits.Length != 4 || !last4Digits.All(char.IsDigit))
            throw new ArgumentException("Last 4 digits must be exactly 4 digits", nameof(last4Digits));

        if (expiryMonth.HasValue && (expiryMonth < 1 || expiryMonth > 12))
            throw new ArgumentException("Expiry month must be between 1 and 12", nameof(expiryMonth));

        if (expiryYear.HasValue && expiryYear < DateTime.UtcNow.Year)
            throw new ArgumentException("Expiry year cannot be in the past", nameof(expiryYear));

        return new SavedPaymentMethod
        {
            CustomerId = customerId,
            Provider = provider,
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            Type = type,
            Last4Digits = last4Digits,
            CardBrand = cardBrand,
            ExpiryMonth = expiryMonth,
            ExpiryYear = expiryYear,
            CardholderName = cardholderName,
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Update display information (cardholder name, expiry).
    /// SECURITY: Cannot update Xendit tokens or last4Digits (immutable for security).
    /// </summary>
    public void UpdateDisplayInfo(string? cardholderName, int? expiryMonth, int? expiryYear)
    {
        if (expiryMonth.HasValue && (expiryMonth < 1 || expiryMonth > 12))
            throw new ArgumentException("Expiry month must be between 1 and 12", nameof(expiryMonth));
        
        if (expiryYear.HasValue && expiryYear < DateTime.UtcNow.Year)
            throw new ArgumentException("Expiry year cannot be in the past", nameof(expiryYear));
        
        CardholderName = cardholderName;
        ExpiryMonth = expiryMonth;
        ExpiryYear = expiryYear;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Set as default payment method.
    /// Note: Caller should unset other defaults first.
    /// </summary>
    public void SetAsDefault()
    {
        IsDefault = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Unset as default payment method.
    /// </summary>
    public void UnsetAsDefault()
    {
        IsDefault = false;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Mark as used (update last used timestamp).
    /// </summary>
    public void MarkAsUsed()
    {
        LastUsedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deactivate payment method (soft delete).
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        IsDefault = false; // Cannot be default if inactive
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reactivate payment method.
    /// </summary>
    public void Reactivate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Check if payment method is expired (based on expiry date).
    /// </summary>
    public bool IsExpired()
    {
        if (!ExpiryYear.HasValue || !ExpiryMonth.HasValue)
            return false; // Cannot determine if no expiry info
        
        var expiryDate = new DateTime(ExpiryYear.Value, ExpiryMonth.Value, 1).AddMonths(1).AddDays(-1);
        return expiryDate < DateTime.UtcNow;
    }
}

/// <summary>
/// Payment method type enum.
/// </summary>
public enum PaymentMethodType
{
    CreditCard,
    DebitCard,
    EWallet
}
