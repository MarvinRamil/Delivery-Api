using BeeLogistics.Modules.Identity.Domain;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>
/// Stamps LastFaceCheckAt when the Verification module completes a successful
/// per-shift face check (same pattern as <see cref="LivenessVerifiedHandler"/>).
/// </summary>
public class ShiftCheckPassedHandler : IOnShiftCheckPassed
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ShiftCheckPassedHandler> _logger;

    public ShiftCheckPassedHandler(UserManager<ApplicationUser> userManager, ILogger<ShiftCheckPassedHandler> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task MarkPassedAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId)
            ?? await _userManager.Users.FirstOrDefaultAsync(u => u.ClerkUserId == userId, cancellationToken);
        if (user == null)
        {
            _logger.LogError("[ShiftCheck] Cannot mark passed — no local user for id {UserId}", userId);
            return;
        }

        user.LastFaceCheckAt = DateTime.UtcNow;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            _logger.LogError("[ShiftCheck] Failed to persist LastFaceCheckAt for user {UserId}: {Errors}",
                user.Id, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
