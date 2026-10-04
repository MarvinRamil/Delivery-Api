using BeeLogistics.Modules.CRM.Application.DTOs;
using BeeLogistics.Modules.CRM.Application.Handlers;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BeeLogistics.Modules.CRM.Presentation.Controllers;

public class TicketsController : BaseController
{
    private readonly IMediator _mediator;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(IMediator mediator, ILogger<TicketsController> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpGet]
    [Authorize(Roles = "SuperAdmin,Admin,Owner,Dispatcher")] // Owner/Dispatcher from frontend, SuperAdmin/Admin from backoffice
    public async Task<IActionResult> GetAll([FromQuery] TicketStatus? status = null)
    {
        var result = await _mediator.Send(new GetTicketsQuery(status));
        return FromResult(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] bool includeZammad = false)
    {
        if (includeZammad)
        {
            var result = await _mediator.Send(new GetTicketWithZammadQuery(id));
            return FromResult(result);
        }
        var ticketResult = await _mediator.Send(new GetTicketQuery(id));
        return FromResult(ticketResult);
    }

    [HttpGet("my")]
    public async Task<IActionResult> GetMyTickets()
    {
        var result = await _mediator.Send(new GetMyTicketsQuery(UserId));
        return FromResult(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketDto dto, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        _logger.LogInformation("Tickets.Create received: Subject={Subject}, UserId={UserId}", dto?.Subject ?? "(null)", UserId ?? "(empty)");
        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? User.FindFirstValue("full_name");

        var result = await _mediator.Send(new CreateTicketCommand(dto, UserId, email, name, idempotencyKey));
        _logger.LogInformation("Tickets.Create completed: Success={Success}", result.IsSuccess);
        return FromResult(result);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Owner,Admin,Dispatcher")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketDto dto)
    {
        var result = await _mediator.Send(new UpdateTicketCommand(id, dto));
        return FromResult(result);
    }

    [HttpPost("{id}/resolve")]
    [Authorize(Roles = "Owner,Admin,Dispatcher")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveTicketRequest request)
    {
        var result = await _mediator.Send(new ResolveTicketCommand(id, request.Resolution));
        return FromResult(result);
    }

    [HttpPost("{id}/comments")]
    public async Task<IActionResult> AddComment(Guid id, [FromBody] AddTicketCommentDto dto)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Body))
            return BadRequest(new { message = "Comment body is required" });

        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue("name") ?? User.FindFirstValue("full_name");
        var result = await _mediator.Send(new AddTicketCommentCommand(id, dto.Body, UserId, email, name));
        return FromResult(result);
    }
}

public record ResolveTicketRequest(string Resolution);
