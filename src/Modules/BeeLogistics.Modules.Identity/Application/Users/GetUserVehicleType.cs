using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace BeeLogistics.Modules.Identity.Application.Users;

/// <summary>
/// Serves <see cref="GetUserVehicleTypeQuery"/> for modules that do not reference Identity.
/// </summary>
public class GetUserVehicleTypeQueryHandler : IRequestHandler<GetUserVehicleTypeQuery, Result<string?>>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public GetUserVehicleTypeQueryHandler(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<Result<string?>> Handle(GetUserVehicleTypeQuery request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty)
            return Result.Fail<string?>("User id is required");

        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            return Result.NotFound<string?>("User not found");

        // Blank is normalised to null so callers only have one "nothing on file" case to handle.
        return Result.Ok(string.IsNullOrWhiteSpace(user.VehicleType) ? null : user.VehicleType);
    }
}
