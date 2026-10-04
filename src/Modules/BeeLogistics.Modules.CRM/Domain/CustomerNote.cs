using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.CRM.Domain;

public class CustomerNote : Entity
{
    public Guid CustomerProfileId { get; set; }
    public string CreatedByUserId { get; set; } = null!;
    public string CreatedByName { get; set; } = null!;
    public string Content { get; set; } = null!;
    public bool IsPinned { get; set; }

    // Navigation
    public CustomerProfile CustomerProfile { get; set; } = null!;
}
