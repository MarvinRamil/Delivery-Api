using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>The driver's primary vehicle, flattened for response building.</summary>
public sealed record PrimaryVehicleInfo(
    Guid VehicleId,
    string PlateNumber,
    string? Model,
    string? Color,
    string? Type,
    bool IsPrimary
);

public interface IDriverVehicleService
{
    /// <summary>
    /// The driver's primary vehicle, or their most recently assigned one if none is flagged primary.
    /// </summary>
    Task<PrimaryVehicleInfo?> GetPrimaryVehicleInfoAsync(string driverId, CancellationToken ct = default);

    /// <summary>
    /// Mark exactly one of the driver's assignments primary. Does not save - the caller owns the
    /// unit of work, which is what lets it be composed with other writes in one SaveChanges.
    /// </summary>
    Task SetSinglePrimaryVehicleAsync(string driverId, Guid vehicleId, CancellationToken ct = default);

    /// <summary>
    /// Sync the legacy <c>ApplicationUser.Vehicle*</c> fields into the relational Vehicle /
    /// DriverVehicleAssignment model, creating or updating as needed. No-op when the user has
    /// no plate. Saves.
    /// </summary>
    Task UpsertPrimaryVehicleAssignmentAsync(ApplicationUser user, CancellationToken ct = default);
}

public class DriverVehicleService : IDriverVehicleService
{
    private readonly IdentityAppDbContext _db;

    public DriverVehicleService(IdentityAppDbContext db)
    {
        _db = db;
    }

    public async Task<PrimaryVehicleInfo?> GetPrimaryVehicleInfoAsync(string driverId, CancellationToken ct = default)
    {
        return await _db.DriverVehicleAssignments
            .AsNoTracking()
            .Where(x => x.DriverId == driverId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenByDescending(x => x.AssignedAt)
            .Select(x => new PrimaryVehicleInfo(
                x.Vehicle.Id,
                x.Vehicle.PlateNumber,
                x.Vehicle.Model,
                x.Vehicle.Color,
                x.Vehicle.Type,
                x.IsPrimary
            ))
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetSinglePrimaryVehicleAsync(string driverId, Guid vehicleId, CancellationToken ct = default)
    {
        var assignments = await _db.DriverVehicleAssignments
            .Where(x => x.DriverId == driverId)
            .ToListAsync(ct);

        foreach (var item in assignments)
        {
            item.IsPrimary = item.VehicleId == vehicleId;
        }
    }

    public async Task UpsertPrimaryVehicleAssignmentAsync(ApplicationUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user.VehiclePlate))
        {
            return;
        }

        var normalizedPlate = user.VehiclePlate.Trim().ToUpperInvariant();
        var vehicle = await _db.Vehicles.FirstOrDefaultAsync(v => v.PlateNumber == normalizedPlate, ct);
        if (vehicle == null)
        {
            vehicle = new Vehicle
            {
                PlateNumber = normalizedPlate,
                Model = user.VehicleModel,
                Color = user.VehicleColor,
                Type = user.VehicleType
            };
            _db.Vehicles.Add(vehicle);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(user.VehicleModel)) vehicle.Model = user.VehicleModel;
            if (!string.IsNullOrWhiteSpace(user.VehicleColor)) vehicle.Color = user.VehicleColor;
            if (!string.IsNullOrWhiteSpace(user.VehicleType)) vehicle.Type = user.VehicleType;
        }

        var assignment = await _db.DriverVehicleAssignments
            .FirstOrDefaultAsync(x => x.DriverId == user.Id && x.VehicleId == vehicle.Id, ct);
        if (assignment == null)
        {
            _db.DriverVehicleAssignments.Add(new DriverVehicleAssignment
            {
                DriverId = user.Id,
                Vehicle = vehicle,
                IsPrimary = true
            });
        }

        await SetSinglePrimaryVehicleAsync(user.Id, vehicle.Id, ct);
        await _db.SaveChangesAsync(ct);
    }
}
