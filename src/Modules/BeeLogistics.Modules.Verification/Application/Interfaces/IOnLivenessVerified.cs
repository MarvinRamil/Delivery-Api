namespace BeeLogistics.Modules.Verification.Application.Interfaces;

/// <summary>
/// Called by the Verification module when a user completes liveness successfully.
/// Implement in Identity (or API) to set LivenessVerifiedAt on the user.
/// Keeps Verification independent so it can be extracted to a microservice later.
/// </summary>
public interface IOnLivenessVerified
{
    Task MarkVerifiedAsync(string userId, CancellationToken cancellationToken = default);
}
