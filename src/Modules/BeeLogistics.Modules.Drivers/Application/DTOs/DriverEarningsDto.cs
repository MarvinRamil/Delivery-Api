namespace BeeLogistics.Modules.Drivers.Application.DTOs;

public record DriverEarningsDto(
    decimal Today,
    decimal ThisWeek,
    decimal ThisMonth,
    decimal Total,
    IReadOnlyList<EarningsBreakdownDto> Breakdown
);

public record EarningsBreakdownDto(
    DateTime Date,
    decimal Amount,
    int BookingsCount
);

/// <summary>
/// Single row for earnings history with 5% platform fee breakdown (for driver verification).
/// </summary>
public record EarningsHistoryItemDto(
    Guid BookingId,
    DateTime Date,
    string PaymentMethod,
    decimal GrossAmount,
    decimal PlatformFeePercent,
    decimal PlatformFeeAmount,
    decimal NetAmount
);

/// <summary>
/// Response for GET /api/drivers/{driverId}/earnings/history
/// </summary>
public record DriverEarningsHistoryDto(
    IReadOnlyList<EarningsHistoryItemDto> Items,
    decimal TotalGross,
    decimal TotalPlatformFee,
    decimal TotalNet
);
