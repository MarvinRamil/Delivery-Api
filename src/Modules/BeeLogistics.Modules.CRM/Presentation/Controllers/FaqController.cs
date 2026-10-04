using BeeLogistics.Modules.CRM.Application.DTOs;
using BeeLogistics.Modules.CRM.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.CRM.Presentation.Controllers;

[Route("api/[controller]")]
[ApiController]
public class FaqController : BaseController
{
    private readonly IMediator _mediator;
    public FaqController(IMediator mediator) => _mediator = mediator;

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCategories()
    {
        var result = await _mediator.Send(new GetFaqCategoriesQuery());
        return FromResult(result);
    }

    [HttpGet("articles")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArticles([FromQuery] string? category = null)
    {
        var result = await _mediator.Send(new GetFaqArticlesQuery(category));
        return FromResult(result);
    }

    [HttpGet("articles/{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetArticle(Guid id)
    {
        var result = await _mediator.Send(new GetFaqArticleQuery(id));
        return FromResult(result);
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<IActionResult> Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("Search query required");
            
        var result = await _mediator.Send(new SearchFaqQuery(q));
        return FromResult(result);
    }

    [HttpPost("articles/{id}/helpful")]
    [AllowAnonymous]
    public async Task<IActionResult> MarkHelpful(Guid id, [FromQuery] bool helpful = true)
    {
        var result = await _mediator.Send(new MarkFaqHelpfulCommand(id, helpful));
        return FromResult(result);
    }

    // Admin endpoints
    [HttpPost("articles")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> CreateArticle([FromBody] CreateFaqArticleDto dto)
    {
        var result = await _mediator.Send(new CreateFaqArticleCommand(dto));
        return FromResult(result);
    }

    [HttpPut("articles/{id}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> UpdateArticle(Guid id, [FromBody] UpdateFaqArticleDto dto)
    {
        var result = await _mediator.Send(new UpdateFaqArticleCommand(id, dto));
        return FromResult(result);
    }

    [HttpDelete("articles/{id}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> DeleteArticle(Guid id)
    {
        var result = await _mediator.Send(new DeleteFaqArticleCommand(id));
        return FromResult(result);
    }
}
