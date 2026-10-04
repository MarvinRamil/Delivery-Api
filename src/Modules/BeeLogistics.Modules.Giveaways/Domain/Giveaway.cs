using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Domain;

public class Giveaway : Entity
{
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public string? ImagePath { get; private set; }
    public string? RewardDetails { get; private set; }
    public bool IsActive { get; private set; } = true;
    
    public GiveawayEntryMode EntryMode { get; private set; }
    public int MaxEntriesPerDriver { get; private set; } = 1;
    public string? DtiPermitNumber { get; private set; }
    public string? DtiPermitImagePath { get; private set; }
    public GiveawayStatus Status { get; private set; } = GiveawayStatus.Draft;

    private readonly List<GiveawayPrize> _prizes = new();
    public IReadOnlyCollection<GiveawayPrize> Prizes => _prizes.AsReadOnly();

    private Giveaway() { }

    public Giveaway(
        string title,
        string description,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? rewardDetails,
        GiveawayEntryMode entryMode = GiveawayEntryMode.Manual,
        int maxEntriesPerDriver = 1,
        string? dtiPermitNumber = null,
        string? dtiPermitImagePath = null)
    {
        SetDetails(title, description, startDate, endDate, imagePath, rewardDetails, entryMode, maxEntriesPerDriver, dtiPermitNumber, dtiPermitImagePath);
        IsActive = true;
        Status = GiveawayStatus.Active;
        CreatedAt = DateTime.UtcNow;
    }

    public void Update(
        string title,
        string description,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? rewardDetails,
        bool isActive,
        GiveawayEntryMode entryMode,
        int maxEntriesPerDriver,
        string? dtiPermitNumber,
        string? dtiPermitImagePath)
    {
        SetDetails(title, description, startDate, endDate, imagePath, rewardDetails, entryMode, maxEntriesPerDriver, dtiPermitNumber, dtiPermitImagePath);
        IsActive = isActive;
        if (!isActive && Status == GiveawayStatus.Active)
            Status = GiveawayStatus.Closed;
        else if (isActive && Status == GiveawayStatus.Closed)
            Status = GiveawayStatus.Active;
            
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsDrawn()
    {
        Status = GiveawayStatus.Drawn;
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    private void SetDetails(
        string title,
        string description,
        DateTime startDate,
        DateTime endDate,
        string? imagePath,
        string? rewardDetails,
        GiveawayEntryMode entryMode,
        int maxEntriesPerDriver,
        string? dtiPermitNumber,
        string? dtiPermitImagePath)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Title is required.");
        if (endDate < startDate)
            throw new InvalidOperationException("End date must be greater than or equal to start date.");
        if (maxEntriesPerDriver < 1)
            throw new InvalidOperationException("Max entries per driver must be at least 1.");

        Title = title.Trim();
        Description = description?.Trim() ?? string.Empty;
        StartDate = startDate;
        EndDate = endDate;
        ImagePath = imagePath;
        RewardDetails = rewardDetails?.Trim();
        EntryMode = entryMode;
        MaxEntriesPerDriver = maxEntriesPerDriver;
        DtiPermitNumber = dtiPermitNumber?.Trim();
        DtiPermitImagePath = dtiPermitImagePath;
    }
}
