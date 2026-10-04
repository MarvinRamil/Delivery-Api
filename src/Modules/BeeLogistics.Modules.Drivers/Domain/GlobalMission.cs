using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public class GlobalMission : Entity
{
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal Reward { get; private set; }
    public int Target { get; private set; }
    public MissionType Type { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    private GlobalMission() { }

    public GlobalMission(
        string title,
        string description,
        decimal reward,
        int target,
        MissionType type,
        DateTime expiresAt)
    {
        Update(title, description, reward, target, type, expiresAt, true);
        CreatedAt = DateTime.UtcNow;
    }

    public void Update(
        string title,
        string description,
        decimal reward,
        int target,
        MissionType type,
        DateTime expiresAt,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Title is required.");
        if (target <= 0)
            throw new InvalidOperationException("Target must be greater than zero.");
        if (reward < 0)
            throw new InvalidOperationException("Reward cannot be negative.");

        Title = title.Trim();
        Description = description?.Trim() ?? string.Empty;
        Reward = reward;
        Target = target;
        Type = type;
        ExpiresAt = expiresAt;
        IsActive = isActive;
        UpdatedAt = DateTime.UtcNow;
    }
}
