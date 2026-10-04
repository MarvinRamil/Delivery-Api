namespace BeeLogistics.Modules.Bookings.Application.Interfaces;

/// <summary>
/// Driver display info for use in booking DTOs (name, phone, vehicle, profile picture).
/// </summary>
public record DriverDisplayInfo(
    string? Name,
    string? Phone,
    string? VehiclePlate,
    string? VehicleModel,
    string? VehicleColor,
    string? VehicleType,
    string? ProfilePictureUrl = null
);

/// <summary>
/// Resolves driver display info for use in booking DTOs.
/// </summary>
public interface IDriverDisplayInfoProvider
{
    Task<DriverDisplayInfo?> GetAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>
    /// Resolves display info for multiple drivers in one call (e.g. when mapping a list of bookings).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DriverDisplayInfo>> GetManyAsync(IEnumerable<Guid> driverIds, CancellationToken ct = default);
}
