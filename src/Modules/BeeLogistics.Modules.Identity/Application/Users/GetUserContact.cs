using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace BeeLogistics.Modules.Identity.Application.Users;

/// <summary>
/// Serves <see cref="GetUserContactQuery"/> for modules that do not reference Identity.
/// </summary>
public class GetUserContactQueryHandler : IRequestHandler<GetUserContactQuery, Result<UserContactInfo>>
{
    private readonly UserManager<ApplicationUser> _userManager;

    public GetUserContactQueryHandler(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<Result<UserContactInfo>> Handle(GetUserContactQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Result.Fail<UserContactInfo>("User id is required");

        var user = await _userManager.FindByIdAsync(request.UserId);
        if (user == null)
            return Result.NotFound<UserContactInfo>("User not found");

        return Result.Ok(new UserContactInfo(user.Id.ToString(), user.Email, user.FullName));
    }
}
