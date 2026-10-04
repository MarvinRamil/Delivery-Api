using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;

// ICustomerIdentityResolver: resolves Bookings Customer.Id → Identity UserId for SignalR routing

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

/// <summary>
/// Query to get available drivers for a booking.
/// Follows Single Responsibility Principle (SRP).
/// </summary>
public record GetAvailableDriversQuery(
    Guid BookingId
) : IRequest<Result<IReadOnlyList<AvailableDriverDto>>>;

/// <summary>
/// DTO for available driver information.
/// </summary>
public record AvailableDriverDto(
    Guid DriverId,
    string DriverName,
    decimal? DistanceKm,
    decimal? Rating,
    bool IsFavouriteDriver,
    int? EstimatedArrivalMinutes,
    string? ProfilePictureUrl
);

/// <summary>
/// Handler for getting available drivers.
/// Follows Dependency Inversion Principle (DIP).
/// </summary>
public class GetAvailableDriversQueryHandler : IRequestHandler<GetAvailableDriversQuery, Result<IReadOnlyList<AvailableDriverDto>>>
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IDriverAvailabilityService _driverAvailabilityService;
    private readonly IFavouriteDriverRepository _favouriteDriverRepository;
    private readonly IMediator _mediator;

    public GetAvailableDriversQueryHandler(
        IBookingRepository bookingRepository,
        IDriverAvailabilityService driverAvailabilityService,
        IFavouriteDriverRepository favouriteDriverRepository,
        IMediator mediator)
    {
        _bookingRepository = bookingRepository;
        _driverAvailabilityService = driverAvailabilityService;
        _favouriteDriverRepository = favouriteDriverRepository;
        _mediator = mediator;
    }

    public async Task<Result<IReadOnlyList<AvailableDriverDto>>> Handle(GetAvailableDriversQuery request, CancellationToken ct)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId, ct);
        if (booking == null)
            return Result.NotFound<IReadOnlyList<AvailableDriverDto>>("Booking not found");

        if (booking.Status != BookingStatus.Pending)
            return Result.Fail<IReadOnlyList<AvailableDriverDto>>("Booking is not pending");

        // Get available drivers (simplified - would need to integrate with driver location service)
        // For now, return empty list - this would be implemented with actual driver matching logic
        var availableDrivers = new List<AvailableDriverDto>();

        // Get favourite drivers if customer has any
        var favouriteDrivers = await _favouriteDriverRepository.GetByCustomerIdAsync(booking.CustomerId, ct);
        var favouriteDriverIds = favouriteDrivers.Select(fd => fd.DriverId).ToHashSet();

        // TODO: Integrate with driver location service to get nearby available drivers
        // TODO: Get driver ratings from Rating module
        // TODO: Calculate estimated arrival times

        return Result.Ok<IReadOnlyList<AvailableDriverDto>>(availableDrivers);
    }
}
