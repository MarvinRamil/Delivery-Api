using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.DTOs;

public record BookingDto(
    Guid Id,
    string BookingNumber,
    Guid CustomerId,
    string CustomerName,
    string PickupLocation,
    string DropoffLocation,
    string TruckType,
    string CargoDescription,
    DateTime ScheduleDate,
    BookingStatus Status,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    BookingSize? Size,
    BookingAssignmentStatus AssignmentStatus,
    // Tenancy was removed in #43. These three are always null now and are kept only so
    // driver builds already on phones keep deserializing the same shape. Drop them once
    // the client cleanup has shipped and old builds have aged out.
    Guid? AssignedToTenantId,
    Guid? AssignedByUserId,
    DateTime? AssignedAt,
    Guid? BeeTenantId,
    decimal? WeightKg,
    decimal? PickupLatitude,
    decimal? PickupLongitude,
    decimal? DropoffLatitude,
    decimal? DropoffLongitude,
    string? ItemImagePath,
    decimal? ItemLengthCm = null,
    decimal? ItemWidthCm = null,
    decimal? ItemHeightCm = null,
    decimal? EstimatedFare = null,
    decimal? FinalFare = null,
    Guid? SelectedDriverId = null,
    string? DriverName = null,
    string? DriverPhone = null,
    DateTime? DriverAssignedAt = null,
    // TODO: Populate via MassTransit from Drivers module (GetDriverInfo request)
    string? DriverVehicle = null,
    string? DriverPlate = null,
    string? DriverVehicleColor = null,
    string? DriverVehicleModel = null,
    string? DriverImageUrl = null,
    List<DeliveryStopDto>? Stops = null,
    List<ProofOfDeliveryDto>? ProofOfDeliveries = null,
    string? CancellationReason = null,
    Guid? CancelledBy = null,
    DateTime? CancelledAt = null,
    // Appended last with a default so clients that neither send nor read it keep deserialising
    // this payload unchanged. Serialises as a string via the global JsonStringEnumConverter.
    DeliveryMode DeliveryMode = DeliveryMode.Regular
);

public record UpdateBookingDto(
    string PickupLocation,
    string DropoffLocation,
    string TruckType,
    string CargoDescription,
    DateTime ScheduleDate,
    string? Notes
);

public record UpdateBookingStatusDto(BookingStatus Status);

// Cancellation DTOs
public enum CancellationReason
{
    CustomerRequest,
    DriverUnavailable,
    NoDriverFound,
    PickupLocationInaccessible,
    DeliveryLocationInaccessible,
    ItemNotReady,
    WeatherConditions,
    VehicleBreakdown,
    Emergency,
    Other
}

public record CancelBookingDto(
    CancellationReason Reason,
    string? CustomReason = null
);

// New DTOs for BEE assignment system
public record ClassifyBookingSizeDto(BookingSize Size);
public record DriverBookingOfferDto(
    Guid Id,
    Guid BookingId,
    Guid DriverId,
    Guid TenantId, // Always Guid.Empty since #43; kept on the wire for older driver builds

    DriverOfferStatus Status,
    DateTime OfferedAt,
    DateTime? RespondedAt,
    DateTime ExpiresAt,
    int SequenceNumber,
    decimal? DistanceKm,
    bool IsFavouriteDriver = false,
    decimal? DriverRating = null,
    int? EstimatedArrivalMinutes = null
);

// Enhanced DTO with booking details and stops
public record DriverBookingOfferWithDetailsDto(
    Guid Id,
    Guid BookingId,
    Guid DriverId,
    Guid? TenantId, // Always null since #43; kept on the wire for older driver builds
    DriverOfferStatus Status,
    DateTime OfferedAt,
    DateTime? RespondedAt,
    DateTime ExpiresAt,
    int SequenceNumber,
    decimal? DistanceKm,
    bool IsFavouriteDriver,
    decimal? DriverRating,
    int? EstimatedArrivalMinutes,
    // Booking details
    string BookingNumber,
    Guid CustomerId,
    string CustomerName,
    string VehicleType,
    string CargoDescription,
    DateTime ScheduleDate,
    BookingStatus BookingStatus,
    string? Notes,
    decimal? WeightKg,
    string? ItemImagePath,
    decimal? ItemLengthCm,
    decimal? ItemWidthCm,
    decimal? ItemHeightCm,
    decimal? EstimatedFare,
    decimal? FinalFare,
    decimal? DistanceKmTotal,
    // Multi-stop information
    List<DeliveryStopDto> Stops,
    // What the driver actually takes home. Optional and last so older driver builds, which
    // neither send nor read it, keep deserialising the payload unchanged (#58).
    OfferEarningDetailsDto? EarningDetails = null,
    /// <summary>So a driver can see what kind of job they are being offered before accepting.</summary>
    DeliveryMode DeliveryMode = DeliveryMode.Regular
);

/// <summary>
/// What a driver earns from an offer, so the accept/decline decision is made on the net rather
/// than the gross fare (#58).
/// </summary>
/// <param name="PaymentMethod">"Cash" or "Online".</param>
/// <param name="PaymentMethodConfirmed">
/// False when no payment record existed yet and the method was assumed. Cash bookings always
/// have one by offer time; online bookings may not.
/// </param>
/// <param name="IsEstimate">
/// True while the quote is based on <c>EstimatedFare</c>. The final fare can differ, and the
/// client must not present this as a promise.
/// </param>
/// <param name="BaseEarnings">The gross fare the split is taken from.</param>
/// <param name="NetEarnings">Base minus deductions.</param>
/// <param name="TotalNetEarnings">
/// What the driver ends up with. Equal to <paramref name="NetEarnings"/> while commission is
/// the only deduction; kept distinct so adding one later is not a breaking change.
/// </param>
public record OfferEarningDetailsDto(
    string PaymentMethod,
    bool PaymentMethodConfirmed,
    string Currency,
    bool IsEstimate,
    decimal BaseEarnings,
    IReadOnlyList<OfferEarningsDeductionDto> Deductions,
    decimal NetEarnings,
    decimal TotalNetEarnings,
    OfferCashSettlementDto? CashSettlement
);

/// <param name="RatePercent">The same rate as a percentage, so clients need not multiply.</param>
public record OfferEarningsDeductionDto(
    string Label,
    decimal Rate,
    decimal RatePercent,
    decimal Amount
);

/// <summary>
/// Present only for cash offers. The driver collects the whole fare and the platform's share is
/// taken from their top-up wallet afterwards, so accepting a cash job creates an obligation the
/// driver should see before they take it.
/// </summary>
public record OfferCashSettlementDto(
    decimal CollectedFromCustomer,
    decimal OwedToPlatform,
    string SettledFrom
);

// DTOs for the multi-stop, driver-broadcast booking model
public record DeliveryStopDto(
    Guid? Id, // Set when returned from API (booking stops); null when sent in create/price requests
    int Sequence,
    string Address,
    string Type, // "Pickup" or "Dropoff"
    string? Status = null,
    DateTime? ArrivedAt = null,
    DateTime? CompletedAt = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    string? ContactName = null,
    string? ContactPhone = null,
    string? Notes = null
);

public record PricingResultDto(
    decimal BaseFare,
    decimal DistanceFare,
    decimal WeightSurcharge,
    decimal PriorityFee,
    decimal HighDemandSurcharge,
    decimal TollFee,
    decimal TotalFare,
    decimal DistanceKm,
    string? Breakdown,
    /// <summary>Positive magnitude already subtracted from <paramref name="TotalFare"/>.</summary>
    decimal PoolingDiscount = 0,
    /// <summary>Which mode this quote priced, so a client can confirm it matches what it asked for.</summary>
    DeliveryMode DeliveryMode = DeliveryMode.Regular
);

/// <summary>
/// The one create-booking payload. Exactly two stops: one pickup and one dropoff.
/// Multiple deliveries are multiple bookings, not multiple stops.
/// </summary>
public record CreateBookingDto(
    Guid CustomerId,
    string VehicleType,
    string CargoDescription,
    DateTime ScheduleDate,
    string ServiceType, // "Immediate" or "Scheduled"
    List<DeliveryStopDto> Stops,
    decimal EstimatedFare,
    decimal? WeightKg = null,
    /// <summary>Ignored. Any priority premium is derived server-side; kept so existing senders do not 400.</summary>
    decimal? PriorityFee = null,
    DateTime? ScheduledDateTime = null,
    string? ScheduledPickupWindow = null,
    Guid? FavouriteDriverId = null,
    string? ItemImagePath = null,
    decimal? ItemLengthCm = null,
    decimal? ItemWidthCm = null,
    decimal? ItemHeightCm = null,
    string? Notes = null,
    /// <summary>Payment method: "Cash" (pay driver on delivery) or "PayOnline" (pay via Xendit). Default null = do not create cash payment.</summary>
    string? PaymentMethod = null,
    /// <summary>
    /// "Regular" | "Pooling" | "OnDemand", case-insensitive. Absent or blank means Regular.
    /// </summary>
    /// <remarks>
    /// A string, not the enum: BookingCreationController deserialises this DTO with its own
    /// JsonSerializerOptions, which has no JsonStringEnumConverter (the global one is only on the
    /// MVC pipeline). An enum member here would fail to bind and silently read as Regular.
    /// Same reason ServiceType above is a string.
    /// </remarks>
    string? DeliveryMode = null
);

public record ProofOfDeliveryDto(
    Guid Id,
    Guid BookingId,
    Guid StopId,
    string? ImagePath,
    string? SignaturePath,
    DateTime DeliveredAt,
    string? RecipientName,
    string? Notes
);

public record TipDto(
    Guid Id,
    Guid BookingId,
    Guid CustomerId,
    Guid DriverId,
    decimal Amount,
    string? Message,
    DateTime TippedAt
);
