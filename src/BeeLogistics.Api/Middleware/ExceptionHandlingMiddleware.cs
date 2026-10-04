using System.Text.Json;
using BeeLogistics.Shared.DTOs;
using FluentValidation;
using static BeeLogistics.Api.Middleware.CorrelationIdMiddleware;

namespace BeeLogistics.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip exception handling for Swagger and Scalar endpoints - let them handle their own errors
        if (context.Request.Path.StartsWithSegments("/swagger") ||
            context.Request.Path.StartsWithSegments("/scalar"))
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (OperationCanceledException)
        {
            // Request was canceled (e.g., client navigated away, React Query canceled request)
            // This is normal behavior and shouldn't be logged as an error
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 499; // Client Closed Request
            }
            // If response has started, we can't change status code, so just return
        }
        catch (ValidationException ex)
        {
            var correlationId = GetOrCreateCorrelationId(context);
            context.Response.Headers.Append("X-Correlation-Id", correlationId);
            await HandleValidationException(context, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access attempt");
            var correlationId = GetOrCreateCorrelationId(context);
            context.Response.Headers.Append("X-Correlation-Id", correlationId);
            await HandleUnauthorizedException(context);
        }
        catch (Exception ex)
        {
            // A09:2021 - Security Logging: Log with correlation ID
            var correlationId = GetOrCreateCorrelationId(context);
            _logger.LogError(ex, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);
            await HandleGenericException(context, correlationId);
        }
    }

    private static async Task HandleValidationException(HttpContext context, ValidationException ex)
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        
        // SECURITY: Ensure security headers are present on error responses
        // (SecurityHeadersMiddleware should have already set them, but ensure they're not removed)
        if (!context.Response.Headers.ContainsKey("X-Content-Type-Options"))
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        }

        var errors = ex.Errors.Select(e => e.ErrorMessage).ToList();
        var response = ApiResponse.Fail(errors);

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static async Task HandleUnauthorizedException(HttpContext context)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";
        
        // SECURITY: Ensure security headers are present on error responses
        if (!context.Response.Headers.ContainsKey("X-Content-Type-Options"))
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        }

        // A01:2021 - Broken Access Control: Generic message
        var response = ApiResponse.Fail("Access denied");

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private async Task HandleGenericException(HttpContext context, string correlationId)
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";

        // Always add correlation ID to response header so clients can report it to support
        context.Response.Headers.Append("X-Correlation-Id", correlationId);

        // SECURITY: Ensure security headers are present on error responses
        if (!context.Response.Headers.ContainsKey("X-Content-Type-Options"))
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        }

        // A09:2021 - Don't expose stack traces in production; include correlation ID in body for support
        var message = _env.IsDevelopment()
            ? $"An error occurred. CorrelationId: {correlationId}"
            : $"An unexpected error occurred. Reference: {correlationId}";

        var response = ApiResponse.Fail(message);

        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(CorrelationIdItemKey, out var value) && value is string id)
            return id;
        return Guid.NewGuid().ToString("N");
    }
}
