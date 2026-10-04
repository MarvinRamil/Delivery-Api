namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>KYC summary safe to embed in admin/review DTOs (no PII from the ID document).</summary>
public sealed record DriverVerificationSummary(
    string Status,
    double? FaceMatchScore,
    double? LivenessScore,
    DateTime? CompletedAt);

/// <summary>
/// Read-only cross-module view of driver KYC state, consumed by the Drivers module's
/// admin review endpoints (optional dependency, same pattern as IOnLivenessVerified).
/// </summary>
public interface IDriverVerificationLookup
{
    Task<DriverVerificationSummary?> GetLatestForUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Batch variant for list endpoints. Keyed by userId; users with no verification are absent.</summary>
    Task<IReadOnlyDictionary<string, DriverVerificationSummary>> GetLatestForUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);
}
