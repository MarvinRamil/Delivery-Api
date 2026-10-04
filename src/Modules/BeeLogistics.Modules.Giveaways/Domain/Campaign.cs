using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Domain;

public class Campaign : Entity
{
    public CampaignType Type { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public string? ImagePath { get; private set; }
    public string? CtaText { get; private set; }
    public string? CtaRoute { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Campaign() { }

    public Campaign(
        CampaignType type,
        string title,
        string body,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? ctaText,
        string? ctaRoute)
    {
        SetDetails(type, title, body, startDate, endDate, imagePath, ctaText, ctaRoute);
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public void Update(
        CampaignType type,
        string title,
        string body,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? ctaText,
        string? ctaRoute,
        bool isActive)
    {
        SetDetails(type, title, body, startDate, endDate, imagePath, ctaText, ctaRoute);
        IsActive = isActive;
        UpdatedAt = DateTime.UtcNow;
    }

    private void SetDetails(
        CampaignType type,
        string title,
        string body,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? ctaText,
        string? ctaRoute)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Title is required.");
        if (endDate < startDate)
            throw new InvalidOperationException("End date must be greater than or equal to start date.");

        Type = type;
        Title = title.Trim();
        Body = body?.Trim() ?? string.Empty;
        StartDate = startDate;
        EndDate = endDate;
        ImagePath = imagePath;
        CtaText = ctaText?.Trim();
        CtaRoute = ctaRoute?.Trim();
    }
}

public enum CampaignType
{
    Giveaway = 0,
    News = 1
}
