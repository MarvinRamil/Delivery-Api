namespace BeeLogistics.Modules.Chat.Domain;

public class ConversationParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string UserId { get; set; } = null!;
    public string UserName { get; set; } = null!;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastReadAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation
    public Conversation Conversation { get; set; } = null!;
}
