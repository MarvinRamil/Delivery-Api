using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// Saved withdrawal method (bank account) for drivers.
/// SECURITY: Account numbers are encrypted at rest and masked in responses.
/// </summary>
public class SavedWithdrawalMethod : Entity
{
    public Guid DriverId { get; private set; }
    
    // Bank account information
    public string BankName { get; private set; } = null!;
    public string BankCode { get; private set; } = null!; // Xendit bank code (e.g., "BPI", "BDO")
    public string AccountNumber { get; private set; } = null!; // Encrypted at rest
    public string AccountHolderName { get; private set; } = null!;
    
    // User preferences
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;
    
    // Metadata
    public DateTime? LastUsedAt { get; private set; }
    
    private SavedWithdrawalMethod() { } // For EF Core

    /// <summary>
    /// Create a new saved withdrawal method.
    /// SECURITY: Account number should be encrypted before storage (handled by infrastructure layer).
    /// </summary>
    public static SavedWithdrawalMethod Create(
        Guid driverId,
        string bankName,
        string bankCode,
        string accountNumber,
        string accountHolderName,
        bool isDefault = false)
    {
        // Validation
        if (driverId == Guid.Empty)
            throw new ArgumentException("Driver ID cannot be empty", nameof(driverId));
        
        if (string.IsNullOrWhiteSpace(bankName))
            throw new ArgumentException("Bank name is required", nameof(bankName));
        
        if (string.IsNullOrWhiteSpace(bankCode))
            throw new ArgumentException("Bank code is required", nameof(bankCode));
        
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new ArgumentException("Account number is required", nameof(accountNumber));
        
        if (string.IsNullOrWhiteSpace(accountHolderName))
            throw new ArgumentException("Account holder name is required", nameof(accountHolderName));
        
        // Validate account number format (basic validation - alphanumeric, reasonable length)
        if (accountNumber.Length < 5 || accountNumber.Length > 50)
            throw new ArgumentException("Account number must be between 5 and 50 characters", nameof(accountNumber));
        
        if (!accountNumber.All(c => char.IsLetterOrDigit(c) || c == '-' || c == ' '))
            throw new ArgumentException("Account number contains invalid characters", nameof(accountNumber));
        
        return new SavedWithdrawalMethod
        {
            DriverId = driverId,
            BankName = bankName,
            BankCode = bankCode,
            AccountNumber = accountNumber, // Will be encrypted by infrastructure layer
            AccountHolderName = accountHolderName,
            IsDefault = isDefault,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Update bank account information.
    /// SECURITY: Account number should be encrypted before storage.
    /// </summary>
    public void UpdateBankInfo(string bankName, string bankCode, string accountNumber, string accountHolderName)
    {
        if (string.IsNullOrWhiteSpace(bankName))
            throw new ArgumentException("Bank name is required", nameof(bankName));
        
        if (string.IsNullOrWhiteSpace(bankCode))
            throw new ArgumentException("Bank code is required", nameof(bankCode));
        
        if (string.IsNullOrWhiteSpace(accountNumber))
            throw new ArgumentException("Account number is required", nameof(accountNumber));
        
        if (string.IsNullOrWhiteSpace(accountHolderName))
            throw new ArgumentException("Account holder name is required", nameof(accountHolderName));
        
        if (accountNumber.Length < 5 || accountNumber.Length > 50)
            throw new ArgumentException("Account number must be between 5 and 50 characters", nameof(accountNumber));
        
        BankName = bankName;
        BankCode = bankCode;
        AccountNumber = accountNumber; // Will be encrypted by infrastructure layer
        AccountHolderName = accountHolderName;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Set as default withdrawal method.
    /// Note: Caller should unset other defaults first.
    /// </summary>
    public void SetAsDefault()
    {
        IsDefault = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Unset as default withdrawal method.
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
    /// Deactivate withdrawal method (soft delete).
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        IsDefault = false; // Cannot be default if inactive
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reactivate withdrawal method.
    /// </summary>
    public void Reactivate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Get masked account number for display (shows last 4 digits only).
    /// SECURITY: Never expose full account number in responses.
    /// </summary>
    public string GetMaskedAccountNumber()
    {
        if (string.IsNullOrEmpty(AccountNumber) || AccountNumber.Length < 4)
            return "****";
        
        var last4 = AccountNumber.Substring(Math.Max(0, AccountNumber.Length - 4));
        return $"****{last4}";
    }
}
