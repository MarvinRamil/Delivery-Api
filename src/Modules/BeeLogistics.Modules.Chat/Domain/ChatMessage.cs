using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Chat.Domain;

public class ChatMessage : Entity
{
    public Guid ConversationId { get; set; }
    public string SenderId { get; set; } = null!;
    public string SenderName { get; set; } = null!;
    public string Content { get; set; } = null!;
    public MessageType Type { get; set; } = MessageType.Text;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsEdited { get; set; }
    public DateTime? EditedAt { get; set; }
    // IsDeleted inherited from Entity

    // Navigation
    public Conversation Conversation { get; set; } = null!;
}

public enum MessageType
{
    Text,
    System,      // "User joined", "Dispatch updated"
    Attachment   // Future: file attachments
}
