namespace BeeLogistics.Modules.Drivers.Application.DTOs;

public record DriverPackageInsuranceFeeConfigDto(
    Guid Id,
    string VehicleType,
    decimal Amount,
    int Version,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record UpsertDriverPackageInsuranceFeeConfigDto(
    string VehicleType,
    decimal Amount
);

public record DriverPackageInsuranceFeeConfigVersionDto(
    Guid Id,
    int Version,
    decimal Amount,
    Guid? ChangedByUserId,
    string? ChangedByUserName,
    DateTime CreatedAt
);

/// <summary>
/// Where a driver stands on their package-insurance coverage: which year is next due, its price
/// for their vehicle type, and the coverage window already paid for.
/// </summary>
public record DriverPackageInsuranceStatusDto(
    Guid DriverId,
    string? VehicleType,
    /// <summary>Price for the next unpaid year. Null when no rate is configured for the vehicle type.</summary>
    decimal? AmountDue,
    int PaidThroughYearNumber,
    DateTime? CoverageStartDate,
    DateTime? CoverageEndDate,
    string Status
);

/// <summary>
/// A QR Ph code that pays a driver's next-due package-insurance premium into the platform wallet.
/// Same shape as <c>CashBondQrDto</c> — payment follows the identical flow.
/// </summary>
public record PackageInsuranceQrDto(
    string QrString,
    string QrImage,
    decimal Amount,
    string ReferenceLabel,
    DateTime? ExpiresAt,
    int PolicyYearNumber
);
