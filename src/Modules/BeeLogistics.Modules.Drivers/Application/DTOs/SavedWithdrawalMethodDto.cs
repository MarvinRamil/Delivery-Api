namespace BeeLogistics.Modules.Drivers.Application.DTOs;

/// <summary>
/// DTO for saved withdrawal method.
/// SECURITY: Account number is masked (shows last 4 digits only).
/// </summary>
public record SavedWithdrawalMethodDto(
    Guid Id,
    Guid DriverId,
    string BankName,
    string BankCode,
    string MaskedAccountNumber, // Masked: shows only last 4 digits
    string AccountHolderName,
    bool IsDefault,
    bool IsActive,
    DateTime? LastUsedAt,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// DTO for creating a saved withdrawal method.
/// SECURITY: Account number will be encrypted at rest.
/// </summary>
public record CreateSavedWithdrawalMethodDto(
    string BankName,
    string BankCode, // Xendit bank code
    string AccountNumber, // Will be encrypted
    string AccountHolderName,
    bool IsDefault = false
);

/// <summary>
/// DTO for updating a saved withdrawal method.
/// SECURITY: Account number will be encrypted at rest.
/// </summary>
public record UpdateSavedWithdrawalMethodDto(
    string? BankName = null,
    string? BankCode = null,
    string? AccountNumber = null, // Will be encrypted
    string? AccountHolderName = null,
    bool? IsDefault = null
);
