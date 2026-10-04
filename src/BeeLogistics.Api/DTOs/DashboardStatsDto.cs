using BeeLogistics.Modules.Bookings.Application.DTOs;

namespace BeeLogistics.Api.DTOs;

/// <summary>
/// Dashboard statistics for the on-demand delivery platform.
/// Simplified from fleet-based model - no trucks/dispatches/manifests.
/// </summary>
public record DashboardStatsDto(
    // Bookings
    int TotalBookings,
    int PendingBookings,
    int ActiveBookings,
    int CompletedBookings,
    int CancelledBookings,
    
    // Recent Bookings (last 5)
    IReadOnlyList<BookingDto> RecentBookings
);
