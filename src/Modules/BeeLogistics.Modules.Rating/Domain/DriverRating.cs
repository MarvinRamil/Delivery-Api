using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Rating.Domain;

/// <summary>
/// Aggregated driver rating statistics
/// This can be a materialized view or a table that's updated when ratings are added
/// </summary>
public class DriverRating : Entity
{
    public Guid DriverId { get; private set; }
    public decimal AverageRating { get; private set; } // 1.0 - 5.0
    public int TotalRatings { get; private set; }
    public int FiveStarCount { get; private set; }
    public int FourStarCount { get; private set; }
    public int ThreeStarCount { get; private set; }
    public int TwoStarCount { get; private set; }
    public int OneStarCount { get; private set; }
    public DateTime LastUpdatedAt { get; private set; }

    private DriverRating() { }

    public DriverRating(Guid driverId)
    {
        DriverId = driverId;
        AverageRating = 0;
        TotalRatings = 0;
        LastUpdatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void AddRating(int stars)
    {
        if (stars < 1 || stars > 5)
            throw new ArgumentException("Rating must be between 1 and 5 stars", nameof(stars));

        TotalRatings++;
        
        switch (stars)
        {
            case 5: FiveStarCount++; break;
            case 4: FourStarCount++; break;
            case 3: ThreeStarCount++; break;
            case 2: TwoStarCount++; break;
            case 1: OneStarCount++; break;
        }

        // Recalculate average
        var totalStars = (FiveStarCount * 5) + (FourStarCount * 4) + (ThreeStarCount * 3) + 
                        (TwoStarCount * 2) + (OneStarCount * 1);
        AverageRating = TotalRatings > 0 ? (decimal)totalStars / TotalRatings : 0;
        
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RemoveRating(int stars)
    {
        if (stars < 1 || stars > 5)
            throw new ArgumentException("Rating must be between 1 and 5 stars", nameof(stars));

        if (TotalRatings <= 0)
            return;

        TotalRatings--;

        switch (stars)
        {
            case 5: FiveStarCount = Math.Max(0, FiveStarCount - 1); break;
            case 4: FourStarCount = Math.Max(0, FourStarCount - 1); break;
            case 3: ThreeStarCount = Math.Max(0, ThreeStarCount - 1); break;
            case 2: TwoStarCount = Math.Max(0, TwoStarCount - 1); break;
            case 1: OneStarCount = Math.Max(0, OneStarCount - 1); break;
        }

        // Recalculate average
        var totalStars = (FiveStarCount * 5) + (FourStarCount * 4) + (ThreeStarCount * 3) + 
                        (TwoStarCount * 2) + (OneStarCount * 1);
        AverageRating = TotalRatings > 0 ? (decimal)totalStars / TotalRatings : 0;
        
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
