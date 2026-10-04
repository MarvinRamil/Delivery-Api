using BeeLogistics.Modules.Bookings.Application.DTOs;

namespace BeeLogistics.Api.DTOs;

public record CustomerDashboardStatsDto(
    // Bookings
    int TotalBookings,
    int PendingBookings,
    int InTransitBookings,
    int CompletedBookings,
    
    // Recent Bookings (last 5)
    IReadOnlyList<BookingDto> RecentBookings
);

