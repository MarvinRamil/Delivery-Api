using System;

namespace BeeLogistics.Shared.Contracts;

public interface IUserDeletedIntegrationEvent
{
    string UserId { get; }
    string FullName { get; }
    DateTime DeletedAt { get; }
}

public class UserDeletedIntegrationEvent : IUserDeletedIntegrationEvent
{
    public string UserId { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public DateTime DeletedAt { get; init; } = DateTime.UtcNow;
}
