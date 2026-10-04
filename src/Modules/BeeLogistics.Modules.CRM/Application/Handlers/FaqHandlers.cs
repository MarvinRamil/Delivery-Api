using BeeLogistics.Modules.CRM.Application.DTOs;
using BeeLogistics.Modules.CRM.Domain;
using BeeLogistics.Modules.CRM.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.CRM.Application.Handlers;

// Queries
public record GetFaqCategoriesQuery : IRequest<Result<List<FaqCategoryDto>>>;
public record GetFaqArticlesQuery(string? Category = null) : IRequest<Result<List<FaqArticleDto>>>;
public record GetFaqArticleQuery(Guid Id) : IRequest<Result<FaqArticleDto>>;
public record SearchFaqQuery(string Query) : IRequest<Result<List<FaqArticleDto>>>;

// Commands (Admin only)
public record CreateFaqArticleCommand(CreateFaqArticleDto Data) : IRequest<Result<FaqArticleDto>>;
public record UpdateFaqArticleCommand(Guid Id, UpdateFaqArticleDto Data) : IRequest<Result<FaqArticleDto>>;
public record DeleteFaqArticleCommand(Guid Id) : IRequest<Result>;
public record MarkFaqHelpfulCommand(Guid Id, bool IsHelpful) : IRequest<Result>;

// Handlers
public class GetFaqCategoriesHandler : IRequestHandler<GetFaqCategoriesQuery, Result<List<FaqCategoryDto>>>
{
    private readonly CrmDbContext _context;
    public GetFaqCategoriesHandler(CrmDbContext context) => _context = context;

    public async Task<Result<List<FaqCategoryDto>>> Handle(GetFaqCategoriesQuery request, CancellationToken ct)
    {
        var categories = await _context.FaqCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

        var articleCounts = await _context.FaqArticles
            .Where(a => a.IsPublished)
            .GroupBy(a => a.Category)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Category, x => x.Count, ct);

        var dtos = categories.Select(c => new FaqCategoryDto(
            c.Id, c.Name, c.Description, c.Icon, c.SortOrder, c.IsActive,
            articleCounts.GetValueOrDefault(c.Name, 0)
        )).ToList();

        return Result.Ok(dtos);
    }
}

public class GetFaqArticlesHandler : IRequestHandler<GetFaqArticlesQuery, Result<List<FaqArticleDto>>>
{
    private readonly CrmDbContext _context;
    public GetFaqArticlesHandler(CrmDbContext context) => _context = context;

    public async Task<Result<List<FaqArticleDto>>> Handle(GetFaqArticlesQuery request, CancellationToken ct)
    {
        var query = _context.FaqArticles.Where(a => a.IsPublished);

        if (!string.IsNullOrEmpty(request.Category))
            query = query.Where(a => a.Category == request.Category);

        var articles = await query.OrderBy(a => a.SortOrder).ToListAsync(ct);

        var dtos = articles.Select(a => new FaqArticleDto(
            a.Id, a.Title, a.Content, a.Category, a.SortOrder, a.IsPublished,
            a.ViewCount, a.HelpfulCount, a.NotHelpfulCount,
            string.IsNullOrEmpty(a.Tags) ? [] : a.Tags.Split(',').ToList()
        )).ToList();

        return Result.Ok(dtos);
    }
}

public class GetFaqArticleHandler : IRequestHandler<GetFaqArticleQuery, Result<FaqArticleDto>>
{
    private readonly CrmDbContext _context;
    public GetFaqArticleHandler(CrmDbContext context) => _context = context;

    public async Task<Result<FaqArticleDto>> Handle(GetFaqArticleQuery request, CancellationToken ct)
    {
        var article = await _context.FaqArticles.FindAsync([request.Id], ct);
        if (article == null)
            return Result.Fail<FaqArticleDto>("Article not found");

        // Increment view count
        article.ViewCount++;
        await _context.SaveChangesAsync(ct);

        return Result.Ok(new FaqArticleDto(
            article.Id, article.Title, article.Content, article.Category,
            article.SortOrder, article.IsPublished, article.ViewCount,
            article.HelpfulCount, article.NotHelpfulCount,
            string.IsNullOrEmpty(article.Tags) ? [] : article.Tags.Split(',').ToList()
        ));
    }
}

public class SearchFaqHandler : IRequestHandler<SearchFaqQuery, Result<List<FaqArticleDto>>>
{
    private readonly CrmDbContext _context;
    public SearchFaqHandler(CrmDbContext context) => _context = context;

    public async Task<Result<List<FaqArticleDto>>> Handle(SearchFaqQuery request, CancellationToken ct)
    {
        var searchTerm = request.Query.ToLower();

        var articles = await _context.FaqArticles
            .Where(a => a.IsPublished &&
                (a.Title.ToLower().Contains(searchTerm) ||
                 a.Content.ToLower().Contains(searchTerm) ||
                 (a.Tags != null && a.Tags.ToLower().Contains(searchTerm))))
            .OrderBy(a => a.SortOrder)
            .Take(10)
            .ToListAsync(ct);

        var dtos = articles.Select(a => new FaqArticleDto(
            a.Id, a.Title, a.Content, a.Category, a.SortOrder, a.IsPublished,
            a.ViewCount, a.HelpfulCount, a.NotHelpfulCount,
            string.IsNullOrEmpty(a.Tags) ? [] : a.Tags.Split(',').ToList()
        )).ToList();

        return Result.Ok(dtos);
    }
}

public class CreateFaqArticleHandler : IRequestHandler<CreateFaqArticleCommand, Result<FaqArticleDto>>
{
    private readonly CrmDbContext _context;
    public CreateFaqArticleHandler(CrmDbContext context) => _context = context;

    public async Task<Result<FaqArticleDto>> Handle(CreateFaqArticleCommand request, CancellationToken ct)
    {
        // SECURITY: Sanitize user-generated content to prevent XSS attacks
        // FAQ content may contain basic HTML formatting, so use SanitizeHtml
        var sanitizedTitle = InputSanitizer.SanitizePlainText(request.Data.Title, maxLength: 200);
        var sanitizedContent = InputSanitizer.SanitizeHtml(request.Data.Content, maxLength: 10000);
        var sanitizedCategory = InputSanitizer.SanitizePlainText(request.Data.Category, maxLength: 50);
        
        var article = new FaqArticle
        {
            Title = sanitizedTitle,
            Content = sanitizedContent,
            Category = sanitizedCategory,
            SortOrder = request.Data.SortOrder,
            IsPublished = request.Data.IsPublished,
            Tags = request.Data.Tags != null ? string.Join(",", request.Data.Tags.Select(t => InputSanitizer.SanitizePlainText(t, maxLength: 50))) : null
        };

        _context.FaqArticles.Add(article);
        await _context.SaveChangesAsync(ct);

        return Result.Ok(new FaqArticleDto(
            article.Id, article.Title, article.Content, article.Category,
            article.SortOrder, article.IsPublished, 0, 0, 0,
            request.Data.Tags ?? []
        ));
    }
}

public class UpdateFaqArticleHandler : IRequestHandler<UpdateFaqArticleCommand, Result<FaqArticleDto>>
{
    private readonly CrmDbContext _context;
    public UpdateFaqArticleHandler(CrmDbContext context) => _context = context;

    public async Task<Result<FaqArticleDto>> Handle(UpdateFaqArticleCommand request, CancellationToken ct)
    {
        var article = await _context.FaqArticles.FindAsync([request.Id], ct);
        if (article == null)
            return Result.Fail<FaqArticleDto>("Article not found");

        // SECURITY: Sanitize all user-generated content
        if (request.Data.Title != null) article.Title = InputSanitizer.SanitizePlainText(request.Data.Title, maxLength: 200);
        if (request.Data.Content != null) article.Content = InputSanitizer.SanitizeHtml(request.Data.Content, maxLength: 10000);
        if (request.Data.Category != null) article.Category = InputSanitizer.SanitizePlainText(request.Data.Category, maxLength: 50);
        if (request.Data.SortOrder.HasValue) article.SortOrder = request.Data.SortOrder.Value;
        if (request.Data.IsPublished.HasValue) article.IsPublished = request.Data.IsPublished.Value;
        if (request.Data.Tags != null) article.Tags = string.Join(",", request.Data.Tags.Select(t => InputSanitizer.SanitizePlainText(t, maxLength: 50)));

        await _context.SaveChangesAsync(ct);

        return Result.Ok(new FaqArticleDto(
            article.Id, article.Title, article.Content, article.Category,
            article.SortOrder, article.IsPublished, article.ViewCount,
            article.HelpfulCount, article.NotHelpfulCount,
            string.IsNullOrEmpty(article.Tags) ? [] : article.Tags.Split(',').ToList()
        ));
    }
}

public class DeleteFaqArticleHandler : IRequestHandler<DeleteFaqArticleCommand, Result>
{
    private readonly CrmDbContext _context;
    public DeleteFaqArticleHandler(CrmDbContext context) => _context = context;

    public async Task<Result> Handle(DeleteFaqArticleCommand request, CancellationToken ct)
    {
        var article = await _context.FaqArticles.FindAsync([request.Id], ct);
        if (article == null)
            return Result.Fail("Article not found");

        _context.FaqArticles.Remove(article);
        await _context.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

public class MarkFaqHelpfulHandler : IRequestHandler<MarkFaqHelpfulCommand, Result>
{
    private readonly CrmDbContext _context;
    public MarkFaqHelpfulHandler(CrmDbContext context) => _context = context;

    public async Task<Result> Handle(MarkFaqHelpfulCommand request, CancellationToken ct)
    {
        var article = await _context.FaqArticles.FindAsync([request.Id], ct);
        if (article == null)
            return Result.Fail("Article not found");

        if (request.IsHelpful)
            article.HelpfulCount++;
        else
            article.NotHelpfulCount++;

        await _context.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
