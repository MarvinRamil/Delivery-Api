using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public class DriverMission : Entity
{
    public Guid? GlobalMissionId { get; private set; }
    public Guid DriverId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal Reward { get; private set; } // Reward amount in pesos
    public int Progress { get; private set; }
    public int Target { get; private set; }
    public MissionStatus Status { get; private set; } // Active, Available, Completed, Expired
    public MissionType Type { get; private set; } // Weekly, Daily, Special
    public DateTime ExpiresAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? RewardedAt { get; private set; }

    private DriverMission() { } // For EF Core

    public DriverMission(
        Guid driverId,
        string title,
        string description,
        decimal reward,
        int target,
        MissionType type,
        DateTime expiresAt,
        Guid? globalMissionId = null)
    {
        GlobalMissionId = globalMissionId;
        DriverId = driverId;
        Title = title;
        Description = description;
        Reward = reward;
        Target = target;
        Type = type;
        ExpiresAt = expiresAt;
        Progress = 0;
        Status = MissionStatus.Available;
        CreatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        if (Status != MissionStatus.Available)
            throw new InvalidOperationException("Only available missions can be activated");

        Status = MissionStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateProgress(int progress)
    {
        if (Status != MissionStatus.Active)
            throw new InvalidOperationException("Progress can only be updated for active missions");

        if (progress < 0 || progress > Target)
            throw new ArgumentException("Progress must be between 0 and target", nameof(progress));

        Progress = progress;

        if (Progress >= Target)
        {
            Complete();
        }

        UpdatedAt = DateTime.UtcNow;
    }

    public void Complete()
    {
        if (Status != MissionStatus.Active)
            throw new InvalidOperationException("Only active missions can be completed");

        if (Progress < Target)
            throw new InvalidOperationException("Mission target not reached");

        Status = MissionStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ClaimReward()
    {
        if (Status != MissionStatus.Completed)
            throw new InvalidOperationException("Reward can only be claimed for completed missions");

        if (RewardedAt.HasValue)
            throw new InvalidOperationException("Reward already claimed");

        RewardedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Expire()
    {
        if (Status == MissionStatus.Completed)
            return; // Don't expire completed missions

        Status = MissionStatus.Expired;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SyncFromGlobalMission(string title, string description, decimal reward, int target, MissionType type, DateTime expiresAt)
    {
        if (Status == MissionStatus.Completed)
            return;

        Title = title;
        Description = description;
        Reward = reward;
        Target = target;
        Type = type;
        ExpiresAt = expiresAt;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum MissionStatus
{
    Available = 0,  // Available to activate
    Active = 1,     // Currently active and in progress
    Completed = 2,  // Target reached, reward can be claimed
    Expired = 3     // Expired without completion
}

public enum MissionType
{
    Weekly = 0,
    Daily = 1,
    Special = 2
}

