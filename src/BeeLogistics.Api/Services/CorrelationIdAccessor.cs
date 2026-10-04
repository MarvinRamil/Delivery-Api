using BeeLogistics.Api.Middleware;
using BeeLogistics.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Reads the current correlation ID from HttpContext (set by CorrelationIdMiddleware).
/// Returns null when not in an HTTP request (e.g. background job or consumer without forwarded ID).
/// </summary>
public class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetCorrelationId()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context?.Items == null)
            return null;

        return context.Items.TryGetValue(CorrelationIdMiddleware.CorrelationIdItemKey, out var value) && value is string id
            ? id
            : null;
    }
}
