using BeeLogistics.Modules.Chat.Domain;

namespace BeeLogistics.Modules.Chat.Application.DTOs;

public record ConversationDto(
    Guid Id,
    string Title,
    string Type,
    Guid? BookingId,
    Guid? DispatchId,
    DateTime CreatedAt,
    DateTime? LastMessageAt,
    int UnreadCount,
    ChatMessageDto? LastMessage,
    List<ParticipantDto> Participants
);

public record ParticipantDto(
    string UserId,
    string UserName,
    DateTime JoinedAt,
    bool IsActive
);

public record ChatMessageDto(
    Guid Id,
    Guid ConversationId,
    string SenderId,
    string SenderName,
    string Content,
    string Type,
    DateTime SentAt,
    bool IsEdited,
    bool IsDeleted
);

public record CreateConversationDto(
    string Title,
    ConversationType Type,
    List<string> ParticipantIds,
    Guid? BookingId = null,
    Guid? DispatchId = null
);

public record SendMessageDto(
    Guid ConversationId,
    string Content
);

public record UpdateMessageDto(
    Guid MessageId,
    string Content
);
