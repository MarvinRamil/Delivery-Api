using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;

namespace BeeLogistics.Modules.Bookings.Application.Handlers;

// Helper
internal static class BookingMapper
{
    public static BookingDto ToDto(Booking b)
    {
        // Get customer name - handle null Customer, null Name, or empty Name
        var customerName = "Unknown";
        if (b.Customer != null && !string.IsNullOrWhiteSpace(b.Customer.Name))
        {
            customerName = b.Customer.Name;
        }
        else if (b.Customer != null && !string.IsNullOrWhiteSpace(b.Customer.Email))
        {
            // Fallback to email if name is not available
            customerName = b.Customer.Email;
        }

        // Sanitize ItemImagePath - prevent exposing full file system paths
        // Only sanitize if it's clearly a full Windows/Unix path (contains backslashes, colons, or drive letters)
        string? sanitizedImagePath = null;
        if (!string.IsNullOrWhiteSpace(b.ItemImagePath))
        {
            var path = b.ItemImagePath;
            // If it's already a relative path (starts with /) or URL, keep it as-is
            if (path.StartsWith("/") || path.StartsWith("http://") || path.StartsWith("https://"))
            {
                sanitizedImagePath = path;
            }
            // If it contains Windows path indicators (backslash, drive letter) or Unix absolute path
            else if (path.Contains("\\") || (path.Length > 1 && path[1] == ':') || path.StartsWith("C:") || path.StartsWith("D:") || path.StartsWith("/"))
            {
                // Extract just the filename to avoid exposing full system paths
                var fileName = System.IO.Path.GetFileName(path);
                sanitizedImagePath = $"/uploads/{fileName}";
            }
            else
            {
                // Assume it's already a safe relative path
                sanitizedImagePath = path;
            }
        }

        // Map stops for driver (POD upload needs dropoff stop id)
        var stopsDto = b.Stops
            .OrderBy(s => s.Sequence)
            .Select(s => new DeliveryStopDto(
                s.Id,
                s.Sequence,
                s.Address,
                s.Type.ToString(),
                s.Status.ToString(),
                s.ArrivedAt,
                s.CompletedAt,
                s.Latitude,
                s.Longitude,
                s.ContactName,
                s.ContactPhone,
                s.Notes
            ))
            .ToList();

        var proofOfDeliveries = b.ProofOfDeliveries
            .Select(p => new ProofOfDeliveryDto(p.Id, p.BookingId, p.StopId, p.ImagePath, p.SignaturePath, p.DeliveredAt, p.RecipientName, p.Notes))
            .ToList();

        // Driver vehicle/image fields left null; to be populated via MassTransit from Drivers module (GetDriverInfo).
        return new BookingDto(
            b.Id, b.BookingNumber, b.CustomerId,
            customerName,
            b.PickupLocation, b.DropoffLocation,
            b.VehicleType, b.CargoDescription, b.ScheduleDate, b.Status, b.Notes,
            b.CreatedAt, b.UpdatedAt,
            // AssignedToTenantId / AssignedByUserId / BeeTenantId: tenancy was removed in #43.
            // The fields stay on the wire as constant nulls until old driver builds age out.
            b.Size, b.AssignmentStatus, null, null,
            b.AssignedAt, null, b.WeightKg, b.PickupLatitude, b.PickupLongitude,
            b.DropoffLatitude, b.DropoffLongitude, sanitizedImagePath,
            b.ItemLengthCm, b.ItemWidthCm, b.ItemHeightCm,
            b.EstimatedFare, b.FinalFare,
            b.SelectedDriverId, null, null, b.DriverAssignedAt,
            null, null, null, null, null,
            stopsDto,
            proofOfDeliveries,
            b.CancellationReason,
            b.CancelledBy,
            b.CancelledAt,
            b.DeliveryMode
        );
    }

    /// <summary>
    /// Maps booking to DTO with optional resolved driver display info (name, phone, vehicle).
    /// </summary>
    public static BookingDto ToDto(Booking b, DriverDisplayInfo? driverInfo)
    {
        var baseDto = ToDto(b);
        if (driverInfo == null)
            return baseDto;
        var driverVehicle = !string.IsNullOrWhiteSpace(driverInfo.VehicleType)
            ? driverInfo.VehicleType
            : (!string.IsNullOrWhiteSpace(driverInfo.VehicleModel) || !string.IsNullOrWhiteSpace(driverInfo.VehicleColor))
                ? string.Join(" ", new[] { driverInfo.VehicleModel, driverInfo.VehicleColor }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim()
                : null;
        return baseDto with
        {
            DriverName = driverInfo.Name,
            DriverPhone = driverInfo.Phone,
            DriverVehicle = driverVehicle,
            DriverPlate = driverInfo.VehiclePlate,
            DriverVehicleColor = driverInfo.VehicleColor,
            DriverVehicleModel = driverInfo.VehicleModel,
            DriverImageUrl = driverInfo.ProfilePictureUrl
        };
    }
}
