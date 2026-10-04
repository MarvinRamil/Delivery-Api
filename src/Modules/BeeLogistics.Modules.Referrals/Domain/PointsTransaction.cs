using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Referrals.Domain;

public class PointsTransaction : Entity
{
    public Guid UserId { get; private set; }
    public PointsTransactionType Type { get; private set; } // Referral, Conversion, Redemption
    public decimal Points { get; private set; } // Positive for earned, negative for spent
    public decimal BalanceAfter { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public Guid? RelatedReferralId { get; private set; }
    public DateTime TransactionDate { get; private set; }

    private PointsTransaction() { } // For EF Core

    public PointsTransaction(
        Guid userId,
        PointsTransactionType type,
        decimal points,
        decimal balanceAfter,
        string description,
        Guid? relatedReferralId = null)
    {
        UserId = userId;
        Type = type;
        Points = points;
        BalanceAfter = balanceAfter;
        Description = description;
        RelatedReferralId = relatedReferralId;
        TransactionDate = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}

public enum PointsTransactionType
{
    ReferralEarned = 0,      // Earned from referral
    ReferralPending = 1,     // Pending referral (moved to available when completed)
    Redeemed = 2,            // Redeemed for discount
    Expired = 3,             // Points expired (if expiry is added later)
    Adjustment = 4           // Manual adjustment by admin
}

