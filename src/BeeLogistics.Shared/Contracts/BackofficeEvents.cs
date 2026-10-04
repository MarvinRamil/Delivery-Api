namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Events forwarded to the back-office backend as HMAC-signed webhooks
/// (consumed by BeeLogistics.Modules.Integration.BackofficeWebhookConsumer).
/// </summary>
public sealed record DriverApplicationSubmittedEvent(
    Guid ApplicationId,
    string? DriverUserId,
    string DriverName,
    DateTime SubmittedAtUtc
);

public sealed record DriverApplicationReviewedEvent(
    Guid ApplicationId,
    string Status, // "Approved" | "Rejected"
    string ReviewedBy,
    string? Reason,
    DateTime ReviewedAtUtc
);

public sealed record DriverKycCompletedEvent(
    Guid VerificationId,
    string DriverUserId,
    string Status, // "Approved" | "Declined"
    double? FaceMatchScore,
    double? LivenessScore,
    DateTime CompletedAtUtc
);

public sealed record GiveawayWinnerSelectedEvent(
    Guid GiveawayId,
    Guid PrizeId,
    Guid WinnerDriverId,
    DateTime DrawnAtUtc
);

public sealed record MissionCompletedEvent(
    Guid MissionId,
    Guid DriverId,
    string MissionTitle,
    decimal Reward,
    DateTime CompletedAtUtc
);
