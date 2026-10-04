using BeeLogistics.Modules.Chat.Application.DTOs;
using BeeLogistics.Modules.Chat.Domain;

namespace BeeLogistics.Modules.Chat.Application.Mappers;

/// <summary>
/// Centralized mapper for Chat domain entities to DTOs.
/// Eliminates duplicate mapping logic across handlers.
/// </summary>
internal static class ChatMapper
{
    /// <summary>
    /// Maps a ChatMessage to ChatMessageDto
    /// </summary>
    public static ChatMessageDto ToDto(ChatMessage message) => new(
        message.Id,
        message.ConversationId,
        message.SenderId,
        message.SenderName,
        message.IsDeleted ? "[Message deleted]" : message.Content,
        message.Type.ToString(),
        message.SentAt,
        message.IsEdited,
        message.IsDeleted
    );

    /// <summary>
    /// Maps a ConversationParticipant to ParticipantDto
    /// </summary>
    public static ParticipantDto ToDto(ConversationParticipant participant) => new(
        participant.UserId,
        participant.UserName,
        participant.JoinedAt,
        participant.IsActive
    );

    /// <summary>
    /// Maps a Conversation to ConversationDto with user-specific unread count
    /// </summary>
    public static ConversationDto ToDto(Conversation conversation, string userId)
    {
        var participant = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
        var lastMessage = conversation.Messages.FirstOrDefault();
        
        var unreadCount = participant?.LastReadAt != null
            ? conversation.Messages.Count(m => m.SentAt > participant.LastReadAt && m.SenderId != userId)
            : conversation.Messages.Count(m => m.SenderId != userId);

        return new ConversationDto(
            conversation.Id,
            conversation.Title,
            conversation.Type.ToString(),
            conversation.BookingId,
            conversation.DispatchId,
            conversation.CreatedAt,
            conversation.LastMessageAt,
            unreadCount,
            lastMessage != null ? ToDto(lastMessage) : null,
            conversation.Participants.Select(ToDto).ToList()
        );
    }

    /// <summary>
    /// Maps a Conversation to ConversationDto for admin/support view (no user-specific unread)
    /// </summary>
    public static ConversationDto ToDtoForAdmin(Conversation conversation)
    {
        var lastMessage = conversation.Messages.FirstOrDefault();
        
        return new ConversationDto(
            conversation.Id,
            conversation.Title,
            conversation.Type.ToString(),
            conversation.BookingId,
            conversation.DispatchId,
            conversation.CreatedAt,
            conversation.LastMessageAt,
            conversation.Messages.Count(m => !m.IsDeleted), // Total undeleted messages as "unread" for admin
            lastMessage != null ? ToDto(lastMessage) : null,
            conversation.Participants.Select(ToDto).ToList()
        );
    }
}

