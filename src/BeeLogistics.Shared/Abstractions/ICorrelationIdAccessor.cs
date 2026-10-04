namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Provides the current correlation ID for the request or message being processed.
/// Set by CorrelationIdMiddleware (HTTP) or from message headers (consumers).
/// </summary>
public interface ICorrelationIdAccessor
{
    /// <summary>
    /// Gets the current correlation ID, or null if not in an HTTP request or message with a correlation ID.
    /// </summary>
    string? GetCorrelationId();
}
