using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Offers.Domain;

public enum OfferDiscountType
{
    Percentage = 0,
    FixedAmount = 1,
    FreeDelivery = 2
}

public enum OfferAudience
{
    Drivers = 0,
    Customers = 1,
    All = 2
}

/// <summary>
/// Promotional offer served to app users. Authored in the back-office backend
/// (which owns the Id) and pushed here via the integration API — this module
/// only stores and serves published snapshots.
/// </summary>
public class Offer : Entity
{
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public OfferDiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public string? PromoCode { get; private set; }
    public OfferAudience TargetAudience { get; private set; }
    public DateTime StartsAt { get; private set; }
    public DateTime EndsAt { get; private set; }
    public string? ImagePath { get; private set; }
    public bool IsActive { get; private set; }

    private Offer() { }

    public Offer(
        Guid id,
        string title,
        string description,
        OfferDiscountType discountType,
        decimal discountValue,
        string? promoCode,
        OfferAudience targetAudience,
        DateTime startsAt,
        DateTime endsAt,
        string? imagePath,
        bool isActive)
    {
        Id = id; // back-office owns the identifier (idempotent upsert)
        SetDetails(title, description, discountType, discountValue, promoCode,
            targetAudience, startsAt, endsAt, imagePath, isActive);
        CreatedAt = DateTime.UtcNow;
    }

    public void Update(
        string title,
        string description,
        OfferDiscountType discountType,
        decimal discountValue,
        string? promoCode,
        OfferAudience targetAudience,
        DateTime startsAt,
        DateTime endsAt,
        string? imagePath,
        bool isActive)
    {
        SetDetails(title, description, discountType, discountValue, promoCode,
            targetAudience, startsAt, endsAt, imagePath, isActive);
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    private void SetDetails(
        string title,
        string description,
        OfferDiscountType discountType,
        decimal discountValue,
        string? promoCode,
        OfferAudience targetAudience,
        DateTime startsAt,
        DateTime endsAt,
        string? imagePath,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Title is required.");
        if (endsAt < startsAt)
            throw new InvalidOperationException("End date must be greater than or equal to start date.");
        if (discountValue < 0)
            throw new InvalidOperationException("Discount value cannot be negative.");
        if (discountType == OfferDiscountType.Percentage && discountValue > 100)
            throw new InvalidOperationException("Percentage discount cannot exceed 100.");

        Title = title.Trim();
        Description = description?.Trim() ?? string.Empty;
        DiscountType = discountType;
        DiscountValue = discountValue;
        PromoCode = string.IsNullOrWhiteSpace(promoCode) ? null : promoCode.Trim().ToUpperInvariant();
        TargetAudience = targetAudience;
        StartsAt = startsAt;
        EndsAt = endsAt;
        ImagePath = imagePath;
        IsActive = isActive;
    }
}
