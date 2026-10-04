namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>
/// Called by the Verification module when a driver passes a per-shift face check.
/// Implement in Identity to set LastFaceCheckAt on the user.
/// Same cross-module pattern as <see cref="IOnLivenessVerified"/>.
/// </summary>
public interface IOnShiftCheckPassed
{
    Task MarkPassedAsync(string userId, CancellationToken cancellationToken = default);
}
