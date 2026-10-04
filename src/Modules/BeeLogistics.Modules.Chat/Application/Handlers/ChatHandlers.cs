using BeeLogistics.Modules.Chat.Application.DTOs;
using BeeLogistics.Modules.Chat.Application.Mappers;
using BeeLogistics.Modules.Chat.Domain;
using BeeLogistics.Modules.Chat.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Chat.Application.Handlers;

// Queries
public record GetUserConversationsQuery(string UserId) : IRequest<Result<List<ConversationDto>>>;
public record GetConversationQuery(Guid ConversationId, string UserId) : IRequest<Result<ConversationDto>>;
public record GetMessagesQuery(Guid ConversationId, string UserId, int Skip = 0, int Take = 50) : IRequest<Result<List<ChatMessageDto>>>;
public record GetSupportConversationsQuery() : IRequest<Result<List<ConversationDto>>>;

// Commands
public record CreateConversationCommand(CreateConversationDto Data, string CreatorId, string CreatorName) : IRequest<Result<ConversationDto>>;
public record SendMessageCommand(SendMessageDto Data, string SenderId, string SenderName) : IRequest<Result<ChatMessageDto>>;
public record MarkAsReadCommand(Guid ConversationId, string UserId) : IRequest<Result>;
public record DeleteMessageCommand(Guid MessageId, string UserId) : IRequest<Result>;
public record JoinConversationCommand(Guid ConversationId, string UserId, string UserName) : IRequest<Result>;

// Handlers
public class GetUserConversationsHandler : IRequestHandler<GetUserConversationsQuery, Result<List<ConversationDto>>>
{
    private readonly ChatDbContext _context;

    public GetUserConversationsHandler(ChatDbContext context) => _context = context;

    public async Task<Result<List<ConversationDto>>> Handle(GetUserConversationsQuery request, CancellationToken ct)
    {
        var conversations = await _context.Conversations
            .Include(c => c.Participants)
            .Include(c => c.Messages.OrderByDescending(m => m.SentAt).Take(1))
            .AsSplitQuery()
            .Where(c => c.Participants.Any(p => p.UserId == request.UserId && p.IsActive))
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync(ct);

        var dtos = conversations.Select(c => ChatMapper.ToDto(c, request.UserId)).ToList();
        return Result.Ok(dtos);
    }
}

public class GetConversationHandler : IRequestHandler<GetConversationQuery, Result<ConversationDto>>
{
    private readonly ChatDbContext _context;

    public GetConversationHandler(ChatDbContext context) => _context = context;

    public async Task<Result<ConversationDto>> Handle(GetConversationQuery request, CancellationToken ct)
    {
        var conversation = await _context.Conversations
            .Include(c => c.Participants)
            .Include(c => c.Messages.OrderByDescending(m => m.SentAt).Take(1))
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, ct);

        if (conversation == null)
            return Result.Fail<ConversationDto>("Conversation not found");

        if (!conversation.Participants.Any(p => p.UserId == request.UserId))
            return Result.Fail<ConversationDto>("Access denied");

        return Result.Ok(ChatMapper.ToDto(conversation, request.UserId));
    }
}

public class GetMessagesHandler : IRequestHandler<GetMessagesQuery, Result<List<ChatMessageDto>>>
{
    private readonly ChatDbContext _context;

    public GetMessagesHandler(ChatDbContext context) => _context = context;

    public async Task<Result<List<ChatMessageDto>>> Handle(GetMessagesQuery request, CancellationToken ct)
    {
        var isParticipant = await _context.ConversationParticipants
            .AnyAsync(p => p.ConversationId == request.ConversationId && p.UserId == request.UserId, ct);

        if (!isParticipant)
            return Result.Fail<List<ChatMessageDto>>("Access denied");

        var messages = await _context.Messages
            .Where(m => m.ConversationId == request.ConversationId)
            .OrderByDescending(m => m.SentAt)
            .Skip(request.Skip)
            .Take(request.Take)
            .ToListAsync(ct);

        var dtos = messages.Select(ChatMapper.ToDto).Reverse().ToList();
        return Result.Ok(dtos);
    }
}

public class CreateConversationHandler : IRequestHandler<CreateConversationCommand, Result<ConversationDto>>
{
    private readonly ChatDbContext _context;

    public CreateConversationHandler(ChatDbContext context) => _context = context;

    public async Task<Result<ConversationDto>> Handle(CreateConversationCommand request, CancellationToken ct)
    {
        // SECURITY: Sanitize user-generated content
        var sanitizedTitle = InputSanitizer.SanitizePlainText(request.Data.Title, maxLength: 200);
        var sanitizedCreatorName = InputSanitizer.SanitizePlainText(request.CreatorName, maxLength: 100);
        
        var conversation = new Conversation
        {
            Title = sanitizedTitle,
            Type = request.Data.Type,
            BookingId = request.Data.BookingId,
            DispatchId = request.Data.DispatchId
        };

        // Add creator as participant
        conversation.Participants.Add(new ConversationParticipant
        {
            ConversationId = conversation.Id,
            UserId = request.CreatorId,
            UserName = sanitizedCreatorName
        });

        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync(ct);

        return Result.Ok(new ConversationDto(
            conversation.Id,
            conversation.Title,
            conversation.Type.ToString(),
            conversation.BookingId,
            conversation.DispatchId,
            conversation.CreatedAt,
            null,
            0,
            null,
            conversation.Participants.Select(ChatMapper.ToDto).ToList()
        ));
    }
}

public class SendMessageHandler : IRequestHandler<SendMessageCommand, Result<ChatMessageDto>>
{
    private readonly ChatDbContext _context;

    public SendMessageHandler(ChatDbContext context) => _context = context;

    public async Task<Result<ChatMessageDto>> Handle(SendMessageCommand request, CancellationToken ct)
    {
        var isParticipant = await _context.ConversationParticipants
            .AnyAsync(p => p.ConversationId == request.Data.ConversationId && p.UserId == request.SenderId, ct);

        if (!isParticipant)
            return Result.Fail<ChatMessageDto>("Not a participant");

        // SECURITY: Sanitize user-generated content to prevent XSS attacks
        var sanitizedContent = InputSanitizer.SanitizePlainText(request.Data.Content, maxLength: 5000);
        
        var message = new ChatMessage
        {
            ConversationId = request.Data.ConversationId,
            SenderId = request.SenderId,
            SenderName = InputSanitizer.SanitizePlainText(request.SenderName, maxLength: 100),
            Content = sanitizedContent,
            Type = MessageType.Text
        };

        _context.Messages.Add(message);

        // Update conversation last message time
        var conversation = await _context.Conversations.FindAsync([request.Data.ConversationId], ct);
        if (conversation != null)
        {
            conversation.LastMessageAt = message.SentAt;
        }

        await _context.SaveChangesAsync(ct);

        return Result.Ok(ChatMapper.ToDto(message));
    }
}

public class MarkAsReadHandler : IRequestHandler<MarkAsReadCommand, Result>
{
    private readonly ChatDbContext _context;

    public MarkAsReadHandler(ChatDbContext context) => _context = context;

    public async Task<Result> Handle(MarkAsReadCommand request, CancellationToken ct)
    {
        var participant = await _context.ConversationParticipants
            .FirstOrDefaultAsync(p => p.ConversationId == request.ConversationId && p.UserId == request.UserId, ct);

        if (participant == null)
            return Result.Fail("Not a participant");

        participant.LastReadAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

public class DeleteMessageHandler : IRequestHandler<DeleteMessageCommand, Result>
{
    private readonly ChatDbContext _context;

    public DeleteMessageHandler(ChatDbContext context) => _context = context;

    public async Task<Result> Handle(DeleteMessageCommand request, CancellationToken ct)
    {
        var message = await _context.Messages.FindAsync([request.MessageId], ct);

        if (message == null)
            return Result.Fail("Message not found");

        if (message.SenderId != request.UserId)
            return Result.Fail("Can only delete own messages");

        message.IsDeleted = true;
        await _context.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

public class GetSupportConversationsHandler : IRequestHandler<GetSupportConversationsQuery, Result<List<ConversationDto>>>
{
    private readonly ChatDbContext _context;

    public GetSupportConversationsHandler(ChatDbContext context) => _context = context;

    public async Task<Result<List<ConversationDto>>> Handle(GetSupportConversationsQuery request, CancellationToken ct)
    {
        var conversations = await _context.Conversations
            .Include(c => c.Participants)
            .Include(c => c.Messages.OrderByDescending(m => m.SentAt).Take(1))
            .AsSplitQuery()
            .Where(c => c.Type == ConversationType.Support)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync(ct);

        var dtos = conversations.Select(ChatMapper.ToDtoForAdmin).ToList();
        return Result.Ok(dtos);
    }
}

public class JoinConversationHandler : IRequestHandler<JoinConversationCommand, Result>
{
    private readonly ChatDbContext _context;
    private readonly ILogger<JoinConversationHandler> _logger;

    public JoinConversationHandler(ChatDbContext context, ILogger<JoinConversationHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Result> Handle(JoinConversationCommand request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.UserId))
            return Result.Fail("User ID is required");
        
        if (string.IsNullOrEmpty(request.UserName))
            return Result.Fail("User name is required");

        try
        {
            // First check if conversation exists
            var conversationExists = await _context.Conversations
                .AnyAsync(c => c.Id == request.ConversationId, ct);

            if (!conversationExists)
                return Result.Fail("Conversation not found");

            // Check if already a participant by querying the participants table directly
            var existingParticipant = await _context.ConversationParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ConversationId == request.ConversationId && p.UserId == request.UserId, ct);

            if (existingParticipant != null)
            {
                // If inactive, reactivate - reload with tracking for update
                if (!existingParticipant.IsActive)
                {
                    var participantToUpdate = await _context.ConversationParticipants
                        .FirstOrDefaultAsync(p => p.ConversationId == request.ConversationId && p.UserId == request.UserId, ct);
                    
                    if (participantToUpdate != null)
                    {
                        participantToUpdate.IsActive = true;
                        participantToUpdate.JoinedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync(ct);
                    }
                }
                return Result.Ok(); // Already joined
            }

            // Add new participant
            var newParticipant = new ConversationParticipant
            {
                ConversationId = request.ConversationId,
                UserId = request.UserId,
                UserName = request.UserName,
                JoinedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.ConversationParticipants.Add(newParticipant);
            await _context.SaveChangesAsync(ct);

            return Result.Ok();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Handle concurrency exception - participant was added/modified by another request
            _logger.LogWarning(ex, "Concurrency exception joining conversation {ConversationId} for user {UserId}", 
                request.ConversationId, request.UserId);
            
            // Verify they're actually a participant now
            var isParticipant = await _context.ConversationParticipants
                .AsNoTracking()
                .AnyAsync(p => p.ConversationId == request.ConversationId && p.UserId == request.UserId && p.IsActive, ct);
            
            if (isParticipant)
                return Result.Ok(); // Success - they're now a participant
            
            return Result.Fail("Failed to join conversation due to concurrent modification. Please try again.");
        }
        catch (DbUpdateException ex)
        {
            // Handle unique constraint violation (user already a participant)
            if (ex.InnerException?.Message.Contains("unique") == true || 
                ex.InnerException?.Message.Contains("duplicate") == true ||
                ex.InnerException?.Message.Contains("IX_ConversationParticipants_ConversationId_UserId") == true)
            {
                _logger.LogDebug("User {UserId} already participant in conversation {ConversationId}", 
                    request.UserId, request.ConversationId);
                return Result.Ok(); // Already joined, treat as success
            }
            
            _logger.LogError(ex, "Database error joining conversation {ConversationId} for user {UserId}", 
                request.ConversationId, request.UserId);
            return Result.Fail("Failed to join conversation due to a database error.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error joining conversation {ConversationId} for user {UserId}", 
                request.ConversationId, request.UserId);
            return Result.Fail("An unexpected error occurred. Please try again.");
        }
    }
}
