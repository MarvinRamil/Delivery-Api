using BeeLogistics.Shared.Abstractions;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Filters;

/// <summary>
/// Sets X-Correlation-Id on all outgoing messages so consumers and downstream services can trace requests.
/// Uses ICorrelationIdAccessor when publishing from HTTP context; forwards from ConsumeContext when publishing from a consumer.
/// </summary>
public class CorrelationIdSendFilter<T> : IFilter<SendContext<T>> where T : class
{
    private readonly ICorrelationIdAccessor _correlationIdAccessor;
    private readonly ILogger<CorrelationIdSendFilter<T>> _logger;

    public const string CorrelationIdHeaderName = "X-Correlation-Id";

    public CorrelationIdSendFilter(ICorrelationIdAccessor correlationIdAccessor, ILogger<CorrelationIdSendFilter<T>> logger)
    {
        _correlationIdAccessor = correlationIdAccessor;
        _logger = logger;
    }

    public void Probe(ProbeContext context)
    {
        context.CreateFilterScope("correlationId");
    }

    public async Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        var correlationId = _correlationIdAccessor.GetCorrelationId();

        if (string.IsNullOrEmpty(correlationId) && context.TryGetPayload<ConsumeContext>(out var consumeContext))
        {
            if (consumeContext.Headers.TryGetHeader(CorrelationIdHeaderName, out var headerValue) && headerValue is string id)
                correlationId = id;
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            context.Headers.Set(CorrelationIdHeaderName, correlationId);
            _logger.LogDebug("Set {Header} on outgoing message: {CorrelationId}", CorrelationIdHeaderName, correlationId);
        }

        await next.Send(context);
    }
}
