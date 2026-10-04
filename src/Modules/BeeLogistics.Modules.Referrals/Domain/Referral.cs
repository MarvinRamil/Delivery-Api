using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Referrals.Domain;

public class Referral : Entity
{
    public Guid ReferralCodeId { get; private set; }
    public Guid ReferrerId { get; private set; } // Who referred
    public Guid ReferredUserId { get; private set; } // Who was referred
    public ReferralUserType ReferrerType { get; private set; } // Driver or Customer
    public ReferralUserType ReferredType { get; private set; } // Driver or Customer
    public ReferralStatus Status { get; private set; } // Pending, Completed, Rewarded
    public DateTime ReferredAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? RewardedAt { get; private set; }
    public decimal PointsAwarded { get; private set; }
    
    // Navigation
    public ReferralCode ReferralCode { get; private set; } = null!;

    private Referral() { } // For EF Core

    public Referral(
        Guid referralCodeId,
        Guid referrerId,
        Guid referredUserId,
        ReferralUserType referrerType,
        ReferralUserType referredType)
    {
        ReferralCodeId = referralCodeId;
        ReferrerId = referrerId;
        ReferredUserId = referredUserId;
        ReferrerType = referrerType;
        ReferredType = referredType;
        Status = ReferralStatus.Pending;
        ReferredAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        if (Status != ReferralStatus.Pending)
            throw new InvalidOperationException("Only pending referrals can be marked as completed");

        Status = ReferralStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AwardPoints(decimal points)
    {
        if (Status != ReferralStatus.Completed)
            throw new InvalidOperationException("Points can only be awarded to completed referrals");

        Status = ReferralStatus.Rewarded;
        PointsAwarded = points;
        RewardedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum ReferralStatus
{
    Pending = 0,      // Referred user registered but hasn't met criteria yet
    Completed = 1,    // Referred user met completion criteria (first booking/5 deliveries)
    Rewarded = 2      // Points have been awarded to referrer
}

