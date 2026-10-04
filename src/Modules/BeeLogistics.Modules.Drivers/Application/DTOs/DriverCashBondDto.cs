namespace BeeLogistics.Modules.Drivers.Application.DTOs;

public record DriverCashBondConfigDto(
    Guid Id,
    string VehicleType,
    decimal Amount,
    int Version,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record UpsertDriverCashBondConfigDto(
    string VehicleType,
    decimal Amount
);

public record DriverCashBondConfigVersionDto(
    Guid Id,
    int Version,
    decimal Amount,
    Guid? ChangedByUserId,
    string? ChangedByUserName,
    DateTime CreatedAt
);

/// <summary>
/// Where a driver stands on their cashbond: what's due for their vehicle type, and what's
/// currently held.
/// </summary>
public record DriverCashBondStatusDto(
    Guid DriverId,
    string? VehicleType,
    decimal? AmountDue,
    decimal CashBondBalance,
    bool Paid
);

/// <summary>
/// A QR Ph code that pays a driver's cashbond straight into the platform wallet.
/// </summary>
/// <remarks>
/// Deliberately NOT the driver's own child wallet. The cashbond is due before a driver can accept
/// bookings, which is before BeeWallet onboarding has happened, so at this point there is no child
/// account to sweep from — that was the flaw in the first implementation. Paying the platform
/// directly also means the settling webhook arrives on the parent account, and parent webhooks
/// reach us while child ones do not.
/// </remarks>
public record CashBondQrDto(
    string QrString,
    // The same payload rendered as a PNG data URI, so the app shows it with <Image> rather than
    // taking a native QR-rendering dependency — the same choice WalletTopUpQrDto makes.
    string QrImage,
    decimal Amount,
    string ReferenceLabel,
    DateTime? ExpiresAt
);
