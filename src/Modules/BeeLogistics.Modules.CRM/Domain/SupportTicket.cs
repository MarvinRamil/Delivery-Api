using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.CRM.Domain;

public class SupportTicket : Entity
{
    public string TicketNumber { get; set; } = null!;
    public Guid? CustomerProfileId { get; set; }
    public string? UserId { get; set; } // Null for customers, required for drivers
    public string? UserEmail { get; set; } // Stored directly for drivers
    public string? UserFullName { get; set; } // Stored directly for drivers
    public Guid? ConversationId { get; set; } // Links to Chat conversation
    public Guid? BookingId { get; set; } // Optional: related booking
    public string Subject { get; set; } = null!;
    public string? Description { get; set; }
    public TicketCategory Category { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public string? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public int? ZammadTicketId { get; set; }

    /// <summary>How many times we have tried to push this ticket to Zammad.</summary>
    /// <remarks>
    /// Drives the retry backoff and lets a permanently failing ticket be surfaced instead of
    /// retried forever. Zero on tickets created before the retry job existed, which is correct:
    /// they were attempted once inline and never recorded it.
    /// </remarks>
    public int ZammadSyncAttempts { get; set; }

    /// <summary>When the last Zammad sync attempt was made, successful or not.</summary>
    public DateTime? LastZammadSyncAttemptAt { get; set; }

    /// <summary>Why the last sync attempt failed, for diagnosis without trawling logs.</summary>
    public string? LastZammadSyncError { get; set; }

    /// <summary>Highest Zammad article id already pushed to the ticket author.</summary>
    /// <remarks>
    /// Zammad retries webhook deliveries and a trigger can fire more than once for a single
    /// article, so the same reply arrives repeatedly. Article ids are monotonic per instance,
    /// which makes a high-water mark enough to discard repeats without a join table.
    ///
    /// Null means nothing has been ingested yet - correct for every ticket created before this
    /// existed. Those tickets push their next agent reply normally rather than replaying the
    /// whole history at whoever is holding the phone.
    /// </remarks>
    public int? LastZammadArticleId { get; set; }
    public string UserType { get; set; } = "customer"; // "customer" or "driver"
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }

    // Navigation
    public CustomerProfile? CustomerProfile { get; set; }
}

public enum TicketCategory
{
    General,
    Booking,
    Payment,
    Delivery,
    Complaint,
    Feedback
}

public enum TicketPriority
{
    Low,
    Normal,
    High,
    Urgent
}

public enum TicketStatus
{
    Open,
    InProgress,
    WaitingCustomer,
    Resolved,
    Closed
}
