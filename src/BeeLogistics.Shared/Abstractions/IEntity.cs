using System;

namespace BeeLogistics.Shared.Abstractions;

public interface IEntity
{
    Guid Id { get; }
    DateTime CreatedAt { get; }
    DateTime? UpdatedAt { get; }
    bool IsDeleted { get; set; }
}
