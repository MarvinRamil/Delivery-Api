namespace BeeLogistics.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation ID for tracing.
/// Reads X-Correlation-Id from request if present; otherwise generates a new one.
/// Stores in HttpContext.Items and sets X-Correlation-Id on the response.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string CorrelationIdItemKey = "CorrelationId";
    public const string CorrelationIdHeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");

        context.Items[CorrelationIdItemKey] = correlationId;

        // Cross-reference Sentry issues with the Seq entries carrying the same ID. Sentry's own
        // middleware is installed outermost by its startup filter, so its per-request scope is
        // already pushed here and this cannot leak across requests. A tag (not an Extra property)
        // is what Sentry indexes for search. No-op when the SDK is disabled.
        SentrySdk.ConfigureScope(scope => scope.SetTag("correlation_id", correlationId));

        // Register callback BEFORE the response starts so the header is set
        // even for streaming responses (SSE, SignalR WebSocket upgrade, etc.)
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(CorrelationIdHeaderName))
                context.Response.Headers.Append(CorrelationIdHeaderName, correlationId);
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
