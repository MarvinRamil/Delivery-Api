using BeeLogistics.Modules.Drivers.Domain;

namespace BeeLogistics.Modules.Drivers.Application.DTOs;

public record DriverMissionDto(
    Guid Id,
    Guid DriverId,
    string Title,
    string Description,
    decimal Reward,
    int Progress,
    int Target,
    MissionStatus Status,
    MissionType Type,
    DateTime ExpiresAt,
    DateTime? CompletedAt,
    DateTime? RewardedAt
);

