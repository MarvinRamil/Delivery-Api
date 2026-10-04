using BeeLogistics.Modules.Referrals.Domain;

namespace BeeLogistics.Modules.Referrals.Application.DTOs;

public record ReferralCodeDto(
    Guid Id,
    Guid UserId,
    string Code,
    string ReferralLink,
    DateTime CreatedAt
);

public record ReferralDto(
    Guid Id,
    Guid ReferrerId,
    Guid ReferredUserId,
    ReferralStatus Status,
    DateTime CreatedAt,
    DateTime? CompletedAt
);

public record UserPointsDto(
    Guid Id,
    Guid UserId,
    decimal TotalPoints,
    decimal AvailablePoints,
    decimal PendingPoints,
    DateTime LastUpdatedAt
);

public record PointsTransactionDto(
    Guid Id,
    Guid UserId,
    PointsTransactionType Type,
    decimal Points,
    decimal BalanceAfter,
    string Description,
    Guid? RelatedReferralId,
    DateTime TransactionDate
);

public record RedeemPointsDto(
    int Points,
    string Description
);

