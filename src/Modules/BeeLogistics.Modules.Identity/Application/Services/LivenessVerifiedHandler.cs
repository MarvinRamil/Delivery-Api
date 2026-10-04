using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>
/// Marks the user as liveness-verified when the Verification module completes a successful check.
/// </summary>
public class LivenessVerifiedHandler : IOnLivenessVerified
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<LivenessVerifiedHandler> _logger;

    public LivenessVerifiedHandler(UserManager<ApplicationUser> userManager, ILogger<LivenessVerifiedHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task MarkVerifiedAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Expected: userId is the local ApplicationUser.Id (the claims transform swaps
        // NameIdentifier). Fall back to a ClerkUserId lookup so a not-yet-swapped Clerk
        // id can never silently drop the verification.
        var user = await _userManager.FindByIdAsync(userId)
            ?? await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == userId, cancellationToken);
        if (user == null)
        {
            _logger.LogError("[Liveness] Cannot mark verified — no local user for id {UserId}", userId);
            return;
        }

        user.LivenessVerifiedAt = DateTime.UtcNow;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("[Liveness] Failed to persist LivenessVerifiedAt for user {UserId}: {Errors}",
                user.Id, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
