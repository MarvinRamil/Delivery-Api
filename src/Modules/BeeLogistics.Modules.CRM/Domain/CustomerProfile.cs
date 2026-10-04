using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.CRM.Domain;

/// <summary>
/// Extended customer profile for CRM - links to Identity user
/// </summary>
public class CustomerProfile : Entity
{
    public string UserId { get; set; } = null!; // Links to ApplicationUser
    public string Email { get; set; } = null!;
    public string FullName { get; set; } = null!;
    public string? Phone { get; set; }
    public string? CompanyName { get; set; }
    public string? Address { get; set; }
    public CustomerType Type { get; set; } = CustomerType.Individual;
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public string? Notes { get; set; }
    public string? Tags { get; set; } // Comma-separated tags
    public int TotalBookings { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime? LastBookingDate { get; set; }
    public DateTime? LastContactDate { get; set; }

    // Navigation
    public ICollection<SupportTicket> Tickets { get; set; } = new List<SupportTicket>();
    public ICollection<CustomerNote> CustomerNotes { get; set; } = new List<CustomerNote>();
}

public enum CustomerType
{
    Individual,
    Business
}

public enum CustomerStatus
{
    Active,
    Inactive,
    Blocked
}
