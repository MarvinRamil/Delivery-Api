using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Query to calculate fare before booking creation.
/// Follows Single Responsibility Principle (SRP) - only handles fare calculation request.
/// </summary>
public record CalculateFareQuery(
    string VehicleType,
    List<DeliveryStopDto> Stops,
    decimal? WeightKg = null,
    decimal? PriorityFee = null,
    DateTime? ScheduledDateTime = null,
    /// <summary>"Regular" | "Pooling" | "OnDemand". Absent means Regular. Accepted now so clients
    /// can send it; it does not change the fare until mode pricing lands.</summary>
    string? DeliveryMode = null
) : IRequest<Result<PricingResultDto>>;

/// <summary>
/// Handler for calculating delivery fare.
/// Follows Single Responsibility Principle (SRP) - only handles fare calculation.
/// Follows Dependency Inversion Principle (DIP) - depends on IPricingService abstraction.
/// </summary>
public class CalculateFareQueryHandler : IRequestHandler<CalculateFareQuery, Result<PricingResultDto>>
{
    private readonly IPricingService _pricingService;

    public CalculateFareQueryHandler(IPricingService pricingService)
    {
        _pricingService = pricingService;
    }

    public async Task<Result<PricingResultDto>> Handle(CalculateFareQuery request, CancellationToken ct)
    {
        // A booking is exactly one pickup and one dropoff — quoting must agree with what
        // Booking's constructor will accept, or a customer can be quoted a fare they cannot book.
        if (request.Stops == null || request.Stops.Count != 2)
            return Result.Fail<PricingResultDto>("Exactly two stops are required: one pickup and one dropoff");

        var pickupCount = request.Stops.Count(s => s.Type.Equals("Pickup", StringComparison.OrdinalIgnoreCase));
        if (pickupCount != 1)
            return Result.Fail<PricingResultDto>("Exactly one pickup stop is required");

        var dropoffCount = request.Stops.Count(s => s.Type.Equals("Dropoff", StringComparison.OrdinalIgnoreCase));
        if (dropoffCount != 1)
            return Result.Fail<PricingResultDto>("Exactly one dropoff stop is required");

        // Convert DTOs to domain entities
        var stops = request.Stops.Select((s, index) => new DeliveryStop(
            Guid.Empty, // BookingId will be set when booking is created
            index,
            s.Address,
            s.Type.Equals("Pickup", StringComparison.OrdinalIgnoreCase) ? StopType.Pickup : StopType.Dropoff,
            s.Latitude,
            s.Longitude,
            s.ContactName,
            s.ContactPhone,
            s.Notes
        )).ToList();

        // Quoting must agree with what creation will charge, so the mode is parsed the same way
        // and any premium is derived server-side. request.PriorityFee is ignored.
        if (!DeliveryModePolicy.TryParse(request.DeliveryMode, out var deliveryMode))
            return Result.Fail<PricingResultDto>($"Invalid delivery mode: {request.DeliveryMode}");

        var result = await _pricingService.CalculateFareAsync(
            new FareRequest(request.VehicleType, stops, request.WeightKg, request.ScheduledDateTime, deliveryMode),
            ct);

        var dto = new PricingResultDto(
            result.BaseFare,
            result.DistanceFare,
            result.WeightSurcharge,
            result.PriorityFee,
            result.HighDemandSurcharge,
            result.TollFee,
            result.TotalFare,
            result.DistanceKm,
            result.Breakdown,
            result.PoolingDiscount,
            result.DeliveryMode
        );

        return Result.Ok(dto);
    }
}
