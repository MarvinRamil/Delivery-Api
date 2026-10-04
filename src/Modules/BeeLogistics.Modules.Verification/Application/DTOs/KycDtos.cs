namespace BeeLogistics.Modules.Verification.Application.DTOs;

public sealed record CreateKycSessionResult(string SessionId, string VerificationUrl);

/// <summary>Driver-facing verification status. Status is the DriverVerificationStatus name (Pending, InProgress, InReview, Approved, Declined, Abandoned, Expired, Legacy) or "NotStarted".</summary>
public sealed record KycStatusResult(
    string Status,
    double? FaceMatchScore,
    double? LivenessScore,
    string? StatusReason,
    DateTime? CompletedAt)
{
    public const string NotStarted = "NotStarted";
}
