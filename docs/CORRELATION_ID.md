# Correlation ID (Request Tracing)

## Overview

Every API request gets a **correlation ID** so you can trace it across HTTP, MassTransit, and logs. No database or breaking changes.

## Behavior

- **HTTP:** Middleware reads `X-Correlation-Id` from the request or generates a new one, stores it for the request, and sets it on the response header.
- **Errors:** Exception and 404 handlers reuse the same correlation ID when available.
- **MassTransit:** All outgoing messages get the current correlation ID in the `X-Correlation-Id` header (from the HTTP request or forwarded from the consumed message).

## For consumers

When logging inside a consumer, include the correlation ID for traceability:

```csharp
// Optional: read from message headers (same header name as HTTP)
if (context.Headers.TryGetHeader("X-Correlation-Id", out var value) && value is string correlationId)
    _logger.LogInformation("Processing ... CorrelationId: {CorrelationId}", correlationId);
```

Outgoing messages published from that consumer automatically get the same ID via the send filter.

## Client usage

Clients can send `X-Correlation-Id` on the request to chain their own trace ID; the same value is returned on the response and used in logs and messages.
