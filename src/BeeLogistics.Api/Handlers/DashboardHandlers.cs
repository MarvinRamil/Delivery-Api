using BeeLogistics.Api.DTOs;
using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Api.Handlers;

public record GetDashboardStatsQuery(
    DateTime? DateFrom = null,
    DateTime? DateTo = null
) : IRequest<Result<DashboardStatsDto>>;

public class GetDashboardStatsQueryHandler : IRequestHandler<GetDashboardStatsQuery, Result<DashboardStatsDto>>
{
    private readonly IBookingRepository _bookingRepository;

    public GetDashboardStatsQueryHandler(IBookingRepository bookingRepository)
    {
        _bookingRepository = bookingRepository;
    }

    public async Task<Result<DashboardStatsDto>> Handle(GetDashboardStatsQuery request, CancellationToken ct)
    {
        var (totalBookings, pendingBookings, activeBookings, completedBookings, cancelledBookings) =
            await _bookingRepository.GetDashboardCountsAsync(request.DateFrom, request.DateTo, ct);

        var recent = await _bookingRepository.GetRecentForDashboardAsync(request.DateFrom, request.DateTo, 5, ct);
        var recentBookings = recent
            .Select(b => new BookingDto(
                b.Id, b.BookingNumber, b.CustomerId,
                b.Customer?.Name ?? "Unknown",
                b.PickupLocation, b.DropoffLocation,
                b.VehicleType, b.CargoDescription, b.ScheduleDate, b.Status, b.Notes,
                b.CreatedAt, b.UpdatedAt,
                // Tenancy removed in #43; these stay on the wire as constant nulls.
                b.Size, b.AssignmentStatus, null, null,
                b.AssignedAt, null, b.WeightKg, b.PickupLatitude, b.PickupLongitude,
                b.DropoffLatitude, b.DropoffLongitude, b.ItemImagePath,
                b.ItemLengthCm, b.ItemWidthCm, b.ItemHeightCm,
                b.EstimatedFare, b.FinalFare,
                b.SelectedDriverId, null, null, b.DriverAssignedAt,
                null, null, null, null, null,
                null,
                null, // ProofOfDeliveries
                b.CancellationReason,
                b.CancelledBy,
                b.CancelledAt
            ))
            .ToList();

        var stats = new DashboardStatsDto(
            TotalBookings: totalBookings,
            PendingBookings: pendingBookings,
            ActiveBookings: activeBookings,
            CompletedBookings: completedBookings,
            CancelledBookings: cancelledBookings,
            RecentBookings: recentBookings
        );

        return Result.Ok(stats);
    }
}
