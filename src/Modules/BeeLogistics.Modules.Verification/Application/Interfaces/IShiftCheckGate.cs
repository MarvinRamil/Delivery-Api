namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>
/// Decides whether a driver must pass a face check before going online.
/// Consumed by Identity's driver-status endpoint (optional dependency).
/// </summary>
public interface IShiftCheckGate
{
    /// <summary>
    /// True when shift checks are enforced, the driver's last check (from Identity) is missing/stale,
    /// and the driver has a usable face reference from KYC. Drivers without a reference (legacy) are skipped.
    /// </summary>
    Task<bool> RequiresCheckAsync(string userId, DateTime? lastFaceCheckAt, CancellationToken cancellationToken = default);
}
