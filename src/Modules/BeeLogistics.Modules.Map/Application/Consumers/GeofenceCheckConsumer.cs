using BeeLogistics.Modules.Map.Application.Events;
using BeeLogistics.Modules.Map.Application.Services;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Application.Consumers;

/// <summary>
/// Consumer that checks geofences for each location update
/// Detects entry/exit events and publishes GeofenceStateChangedEvent
/// </summary>
public sealed class GeofenceCheckConsumer : IConsumer<DriverLocationUpdatedEvent>
{
    private readonly IGeofenceService _geofenceService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<GeofenceCheckConsumer> _logger;

    public GeofenceCheckConsumer(
        IGeofenceService geofenceService,
        IPublishEndpoint publishEndpoint,
        ILogger<GeofenceCheckConsumer> logger)
    {
        _geofenceService = geofenceService;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<DriverLocationUpdatedEvent> context)
    {
        var locationEvent = context.Message;
        var ct = context.CancellationToken;

        try
        {
            // Get all active geofences
            var geofences = await _geofenceService.GetActiveGeofencesAsync(ct);

            if (geofences.Count == 0)
            {
                // No geofences to check
                return;
            }

            // Check each geofence
            foreach (var geofence in geofences)
            {
                try
                {
                    // Check if driver is inside this geofence
                    var isInside = await _geofenceService.IsPointInGeofenceAsync(
                        locationEvent.Latitude,
                        locationEvent.Longitude,
                        geofence,
                        ct);

                    // Get previous state
                    var previousState = await _geofenceService.GetDriverGeofenceStateAsync(
                        locationEvent.DriverId,
                        geofence.Id,
                        ct);

                    // Update state
                    await _geofenceService.UpdateDriverGeofenceStateAsync(
                        locationEvent.DriverId,
                        geofence.Id,
                        isInside,
                        ct);

                    // Check if state changed
                    if (previousState.HasValue && previousState.Value != isInside)
                    {
                        // State changed - publish event
                        var geofenceEvent = new DriverGeofenceStateChangedEvent
                        {
                            DriverId = locationEvent.DriverId,
                            GeofenceId = geofence.Id,
                            GeofenceName = geofence.Name,
                            EventType = isInside ? GeofenceEventType.Entered : GeofenceEventType.Exited,
                            Latitude = locationEvent.Latitude,
                            Longitude = locationEvent.Longitude,
                            Timestamp = locationEvent.Timestamp,
                            Category = geofence.Category
                        };

                        await _publishEndpoint.Publish(geofenceEvent, ct);

                        _logger.LogInformation(
                            "Driver {DriverId} {EventType} geofence {GeofenceName} ({GeofenceId})",
                            locationEvent.DriverId,
                            geofenceEvent.EventType,
                            geofence.Name,
                            geofence.Id);
                    }
                    else if (!previousState.HasValue && isInside)
                    {
                        // First time checking - driver is inside
                        // Optionally publish "Entered" event for initial state
                        // (Uncomment if you want to track initial entry)
                        /*
                        var geofenceEvent = new DriverGeofenceStateChangedEvent
                        {
                            DriverId = locationEvent.DriverId,
                            GeofenceId = geofence.Id,
                            GeofenceName = geofence.Name,
                            EventType = GeofenceEventType.Entered,
                            Latitude = locationEvent.Latitude,
                            Longitude = locationEvent.Longitude,
                            Timestamp = locationEvent.Timestamp,
                            Category = geofence.Category
                        };

                        await _publishEndpoint.Publish(geofenceEvent, ct);
                        */
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue checking other geofences
                    _logger.LogError(ex,
                        "Error checking geofence {GeofenceId} for driver {DriverId}",
                        geofence.Id,
                        locationEvent.DriverId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error in geofence check consumer for driver {DriverId}",
                locationEvent.DriverId);
            // Don't throw - we don't want to requeue location updates
        }
    }
}

