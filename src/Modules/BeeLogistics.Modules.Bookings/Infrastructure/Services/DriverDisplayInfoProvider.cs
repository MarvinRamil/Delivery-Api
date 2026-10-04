using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Services;

public class DriverDisplayInfoProvider : IDriverDisplayInfoProvider
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IdentityAppDbContext _identityDbContext;

    public DriverDisplayInfoProvider(UserManager<ApplicationUser> userManager, IdentityAppDbContext identityDbContext)
    {
        _userManager = userManager;
        _identityDbContext = identityDbContext;
    }

    public async Task<DriverDisplayInfo?> GetAsync(Guid driverId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(driverId.ToString());
        if (user == null) return null;
        var name = !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName : user.UserName ?? user.Email;
        var primaryVehicle = await _identityDbContext.DriverVehicleAssignments
            .AsNoTracking()
            .Where(x => x.DriverId == user.Id)
            .OrderByDescending(x => x.IsPrimary)
            .ThenByDescending(x => x.AssignedAt)
            .Select(x => new
            {
                x.Vehicle.PlateNumber,
                x.Vehicle.Model,
                x.Vehicle.Color,
                x.Vehicle.Type
            })
            .FirstOrDefaultAsync(ct);

        return new DriverDisplayInfo(
            name,
            user.PhoneNumber,
            primaryVehicle?.PlateNumber ?? user.VehiclePlate,
            primaryVehicle?.Model ?? user.VehicleModel,
            primaryVehicle?.Color ?? user.VehicleColor,
            primaryVehicle?.Type ?? user.VehicleType,
            user.ProfilePictureUrl
        );
    }

    public async Task<IReadOnlyDictionary<Guid, DriverDisplayInfo>> GetManyAsync(IEnumerable<Guid> driverIds, CancellationToken ct = default)
    {
        var ids = driverIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, DriverDisplayInfo>();

        var result = new Dictionary<Guid, DriverDisplayInfo>();
        foreach (var id in ids)
        {
            var info = await GetAsync(id, ct);
            if (info != null)
                result[id] = info;
        }
        return result;
    }
}
