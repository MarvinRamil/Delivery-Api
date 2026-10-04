using BeeLogistics.Api.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;
using MediatR;

namespace BeeLogistics.Api.Handlers;

public record GetCustomerDashboardStatsQuery(
    Guid CustomerId,
    DateTime? DateFrom = null,
    DateTime? DateTo = null
) : IRequest<Result<CustomerDashboardStatsDto>>;

public class GetCustomerDashboardStatsQueryHandler : IRequestHandler<GetCustomerDashboardStatsQuery, Result<CustomerDashboardStatsDto>>
{
    private readonly IBookingRepository _bookingRepository;

    public GetCustomerDashboardStatsQueryHandler(IBookingRepository bookingRepository)
    {
        _bookingRepository = bookingRepository;
    }

    public async Task<Result<CustomerDashboardStatsDto>> Handle(GetCustomerDashboardStatsQuery request, CancellationToken ct)
    {
        // Get all bookings for this customer
        var bookings = await _bookingRepository.GetByCustomerIdAsync(request.CustomerId, ct);

        // Apply date filter if provided
        if (request.DateFrom.HasValue || request.DateTo.HasValue)
        {
            var filteredBookings = bookings.AsQueryable();
            if (request.DateFrom.HasValue)
            {
                filteredBookings = filteredBookings.Where(b => b.CreatedAt >= request.DateFrom.Value);
            }
            if (request.DateTo.HasValue)
            {
                var dateTo = request.DateTo.Value.Date.AddDays(1); // Include the entire day
                filteredBookings = filteredBookings.Where(b => b.CreatedAt < dateTo);
            }
            bookings = filteredBookings.ToList();
        }

        // Calculate booking statistics
        var totalBookings = bookings.Count;
        var pendingBookings = bookings.Count(b => b.Status == BookingStatus.Pending);
        
        // In Transit = bookings that are dispatched
        var inTransitBookings = bookings.Count(b => b.Status == BookingStatus.Dispatched);
        
        var completedBookings = bookings.Count(b => b.Status == BookingStatus.Completed);

        // Get recent bookings (last 5, ordered by CreatedAt descending)
        var recentBookings = bookings
            .OrderByDescending(b => b.CreatedAt)
            .Take(5)
            .Select(b => new BeeLogistics.Modules.Bookings.Application.DTOs.BookingDto(
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

        var stats = new CustomerDashboardStatsDto(
            TotalBookings: totalBookings,
            PendingBookings: pendingBookings,
            InTransitBookings: inTransitBookings,
            CompletedBookings: completedBookings,
            RecentBookings: recentBookings
        );

        return Result.Ok(stats);
    }
}

