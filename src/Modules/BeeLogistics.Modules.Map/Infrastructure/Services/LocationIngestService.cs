using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Events;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Domain;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Infrastructure.Services;

/// <inheritdoc cref="ILocationIngestService"/>
public sealed class LocationIngestService : ILocationIngestService
{
    private readonly ILocationSanitizer _sanitizer;
    private readonly IBus _bus;
    private readonly ILogger<LocationIngestService> _logger;

    public LocationIngestService(
        ILocationSanitizer sanitizer,
        IBus bus,
        ILogger<LocationIngestService> logger)
    {
        _sanitizer = sanitizer;
        // IBus, deliberately, not IPublishEndpoint.
        //
        // Program.cs registers AddEntityFrameworkOutbox<BookingsDbContext> with UseBusOutbox(),
        // which turns the scoped IPublishEndpoint into an *outbox* endpoint: it stages the message
        // onto BookingsDbContext and delivers only when that context is saved. This module never
        // saves BookingsDbContext, and the HTTP handlers touch no database at all, so a message
        // staged from a request scope is silently dropped when the scope disposes.
        //
        // That is exactly what used to happen here (GitLab #37): every location POSTed to
        // /api/locations/update and /api/locations/batch was discarded before it reached RabbitMQ,
        // while the endpoint returned 200 OK. IBus bypasses the outbox and delivers immediately,
        // which is what MqttLocationSubscriberService already did and documented.
        _bus = bus;
        _logger = logger;
    }

    public async Task<LocationIngestResult> IngestAsync(
        Guid driverId,
        DriverLocationUpdateDto location,
        LocationSource source,
        bool isHistorical = false,
        CancellationToken ct = default)
    {
        var sanitization = await _sanitizer.SanitizeAsync(location, driverId, isHistorical, ct);

        if (!sanitization.IsValid)
        {
            var reason = sanitization.RejectionReason?.ToString() ?? "Rejected";
            _logger.LogWarning(
                "[LOCATION] [{Source}] Rejected location for driver {DriverId} (historical: {IsHistorical}). Reason: {Reason}. Errors: {Errors}",
                source, driverId, isHistorical, reason, string.Join("; ", sanitization.Errors));

            return LocationIngestResult.Rejected(
                sanitization.Errors.Count > 0 ? string.Join("; ", sanitization.Errors) : reason);
        }

        if (sanitization.Warnings.Count > 0)
        {
            _logger.LogDebug(
                "[LOCATION] [{Source}] Warnings for driver {DriverId}: {Warnings}",
                source, driverId, string.Join("; ", sanitization.Warnings));
        }

        var sanitized = sanitization.SanitizedLocation!;

        var locationEvent = new DriverLocationUpdatedEvent
        {
            DriverId = driverId,
            Latitude = sanitized.Latitude,
            Longitude = sanitized.Longitude,
            Speed = sanitized.Speed,
            Heading = sanitized.Heading,
            Accuracy = sanitized.Accuracy,
            Timestamp = sanitized.Timestamp,
            DeviceId = sanitized.DeviceId,
            Source = source,
            IsHistorical = isHistorical
        };

        await _bus.Publish(locationEvent, ct);

        _logger.LogInformation(
            "[LOCATION] [{Source}→RabbitMQ] Published location for driver {DriverId} at ({Latitude}, {Longitude}) recorded {Timestamp:yyyy-MM-dd HH:mm:ss.fff} UTC (historical: {IsHistorical})",
            source, driverId, sanitized.Latitude, sanitized.Longitude, sanitized.Timestamp, isHistorical);

        return LocationIngestResult.Ok();
    }
}
