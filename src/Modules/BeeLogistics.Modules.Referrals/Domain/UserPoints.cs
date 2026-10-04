using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Referrals.Domain;

public class UserPoints : Entity
{
    public Guid UserId { get; private set; }
    public decimal TotalPoints { get; private set; } // All points ever earned
    public decimal AvailablePoints { get; private set; } // Can be redeemed (not pending)
    public decimal PendingPoints { get; private set; } // Awaiting completion criteria
    public DateTime LastUpdatedAt { get; private set; }

    private UserPoints() { } // For EF Core

    public UserPoints(Guid userId)
    {
        UserId = userId;
        TotalPoints = 0;
        AvailablePoints = 0;
        PendingPoints = 0;
        LastUpdatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void AddPendingPoints(decimal points)
    {
        if (points <= 0)
            throw new ArgumentException("Points must be positive", nameof(points));

        PendingPoints += points;
        TotalPoints += points;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MovePendingToAvailable(decimal points)
    {
        if (points <= 0)
            throw new ArgumentException("Points must be positive", nameof(points));

        if (PendingPoints < points)
            throw new InvalidOperationException("Insufficient pending points");

        PendingPoints -= points;
        AvailablePoints += points;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RedeemPoints(decimal points)
    {
        if (points <= 0)
            throw new ArgumentException("Points must be positive", nameof(points));

        if (AvailablePoints < points)
            throw new InvalidOperationException("Insufficient available points");

        AvailablePoints -= points;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddAvailablePoints(decimal points)
    {
        if (points <= 0)
            throw new ArgumentException("Points must be positive", nameof(points));

        AvailablePoints += points;
        TotalPoints += points;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}

