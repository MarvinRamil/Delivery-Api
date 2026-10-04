using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Bookings.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Helper
internal static class BookingListProjector
{
    /// <summary>
    /// Maps a list of bookings to DTOs with driver display info attached, resolving stored
    /// image paths to viewable URLs. Driver info is fetched for the whole list in one call
    /// rather than per booking.
    /// </summary>
    /// <remarks>
    /// Used by the customer-facing lists. The driver's own list (GetBookingsByDriverQuery)
    /// deliberately does not use this - it has never carried driver display info, and adding
    /// it would put the driver's own name and photo on every row of their own bookings.
    /// </remarks>
    public static async Task<IReadOnlyList<BookingDto>> ProjectAsync(
        IReadOnlyList<Booking> bookings,
        IDriverDisplayInfoProvider driverDisplayInfoProvider,
        IFileStorageService fileStorage,
        CancellationToken ct)
    {
        var driverIds = bookings.Where(b => b.SelectedDriverId.HasValue).Select(b => b.SelectedDriverId!.Value).Distinct().ToList();
        var driverInfoMap = driverIds.Count > 0
            ? await driverDisplayInfoProvider.GetManyAsync(driverIds, ct)
            : new Dictionary<Guid, DriverDisplayInfo>();

        var dtos = bookings.Select(b =>
        {
            DriverDisplayInfo? info = null;
            if (b.SelectedDriverId.HasValue && driverInfoMap.TryGetValue(b.SelectedDriverId.Value, out var d))
                info = d;
            return BookingMapper.ToDto(b, info);
        }).ToList();

        return await BookingImageUrlResolver.ResolveListAsync(dtos, fileStorage, ct);
    }
}
