using BeeLogistics.Modules.Chat.Application.DTOs;
using BeeLogistics.Modules.Chat.Application.Handlers;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BeeLogistics.Modules.Chat.Presentation.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly IMediator _mediator;

    public ChatHub(IMediator mediator)
    {
        _mediator = mediator;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
        {
            // Join user's personal group for direct messages
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

            // Get user's conversations and join those groups
            var result = await _mediator.Send(new GetUserConversationsQuery(userId));
            if (result.IsSuccess && result.Value != null)
            {
                foreach (var conversation in result.Value)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"chat-{conversation.Id}");
                }
            }
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }

    // Join a specific conversation
    public async Task JoinConversation(Guid conversationId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId)) return;

        var result = await _mediator.Send(new GetConversationQuery(conversationId, userId));
        if (result.IsSuccess)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"chat-{conversationId}");
        }
    }

    // Leave a conversation group
    public async Task LeaveConversation(Guid conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat-{conversationId}");
    }

    // Send a message
    public async Task SendMessage(Guid conversationId, string content)
    {
        var userId = Context.UserIdentifier;
        var userName = Context.User?.FindFirst("full_name")?.Value ?? "Unknown";

        if (string.IsNullOrEmpty(userId)) return;

        var result = await _mediator.Send(new SendMessageCommand(
            new SendMessageDto(conversationId, content),
            userId,
            userName
        ));

        if (result.IsSuccess && result.Value != null)
        {
            // Broadcast to all participants in the conversation
            await Clients.Group($"chat-{conversationId}").SendAsync("ReceiveMessage", result.Value);
        }
    }

    // Mark conversation as read
    public async Task MarkAsRead(Guid conversationId)
    {
        var userId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(userId)) return;

        await _mediator.Send(new MarkAsReadCommand(conversationId, userId));
    }

    // Typing indicator
    public async Task StartTyping(Guid conversationId)
    {
        var userId = Context.UserIdentifier;
        var userName = Context.User?.FindFirst("full_name")?.Value ?? "Unknown";

        await Clients.OthersInGroup($"chat-{conversationId}").SendAsync("UserTyping", new
        {
            ConversationId = conversationId,
            UserId = userId,
            UserName = userName
        });
    }

    public async Task StopTyping(Guid conversationId)
    {
        var userId = Context.UserIdentifier;

        await Clients.OthersInGroup($"chat-{conversationId}").SendAsync("UserStoppedTyping", new
        {
            ConversationId = conversationId,
            UserId = userId
        });
    }
}
