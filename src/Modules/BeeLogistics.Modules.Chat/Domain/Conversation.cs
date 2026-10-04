using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Chat.Domain;

public class Conversation : Entity
{
    public string Title { get; set; } = null!;
    public ConversationType Type { get; set; }
    public Guid? BookingId { get; set; } // Optional: link to a booking
    public Guid? DispatchId { get; set; } // Optional: link to a dispatch
    public DateTime? LastMessageAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
    public ICollection<ConversationParticipant> Participants { get; set; } = new List<ConversationParticipant>();
    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

public enum ConversationType
{
    Direct,      // 1:1 chat
    Group,       // Group chat
    Support,     // Customer support
    Dispatch,    // Dispatch-related (driver, dispatcher, customer)
    Business     // Customer ↔ Fleet Owner/Admin
}
