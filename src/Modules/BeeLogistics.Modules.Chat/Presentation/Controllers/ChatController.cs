using BeeLogistics.Modules.Chat.Application.DTOs;
using BeeLogistics.Modules.Chat.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BeeLogistics.Modules.Chat.Presentation.Controllers;

public class ChatController : BaseController
{
    private readonly IMediator _mediator;

    public ChatController(IMediator mediator) => _mediator = mediator;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub")?.Value ?? "";
    private string UserName => User.FindFirstValue("full_name") ?? 
                               User.FindFirstValue(ClaimTypes.Name) ?? 
                               User.FindFirst("name")?.Value ?? 
                               "Admin";

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations()
    {
        var result = await _mediator.Send(new GetUserConversationsQuery(UserId));
        return FromResult(result);
    }

    [HttpGet("support")]
    [Authorize(Roles = "SuperAdmin,Admin,Owner")] // Owner from frontend, SuperAdmin/Admin from backoffice
    public async Task<IActionResult> GetSupportConversations()
    {
        var result = await _mediator.Send(new GetSupportConversationsQuery());
        return FromResult(result);
    }

    [HttpPost("support/{conversationId}/join")]
    [Authorize(Roles = "SuperAdmin,Admin,Owner")] // Owner from frontend, SuperAdmin/Admin from backoffice
    public async Task<IActionResult> JoinSupportConversation(Guid conversationId)
    {
        if (string.IsNullOrEmpty(UserId))
            return Unauthorized("User ID not found in token");
        
        if (string.IsNullOrEmpty(UserName) || UserName == "Unknown")
        {
            // Try to get name from other claims
            var name = User.FindFirstValue(ClaimTypes.Name) ?? 
                      User.FindFirstValue("name") ?? 
                      User.FindFirstValue("full_name") ?? 
                      "Admin";
            var result = await _mediator.Send(new JoinConversationCommand(conversationId, UserId, name));
            return FromResult(result);
        }
        
        var result2 = await _mediator.Send(new JoinConversationCommand(conversationId, UserId, UserName));
        return FromResult(result2);
    }

    [HttpGet("conversations/{id}")]
    public async Task<IActionResult> GetConversation(Guid id)
    {
        var result = await _mediator.Send(new GetConversationQuery(id, UserId));
        return FromResult(result);
    }

    [HttpGet("conversations/{id}/messages")]
    public async Task<IActionResult> GetMessages(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        var result = await _mediator.Send(new GetMessagesQuery(id, UserId, skip, take));
        return FromResult(result);
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConversationDto dto)
    {
        var result = await _mediator.Send(new CreateConversationCommand(dto, UserId, UserName));
        return FromResult(result);
    }

    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)
    {
        var result = await _mediator.Send(new SendMessageCommand(dto, UserId, UserName));
        return FromResult(result);
    }

    [HttpPost("conversations/{id}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id)
    {
        var result = await _mediator.Send(new MarkAsReadCommand(id, UserId));
        return FromResult(result);
    }

    [HttpDelete("messages/{id}")]
    public async Task<IActionResult> DeleteMessage(Guid id)
    {
        var result = await _mediator.Send(new DeleteMessageCommand(id, UserId));
        return FromResult(result);
    }
}
