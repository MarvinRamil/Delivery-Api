using BeeLogistics.Modules.Map.Application.DTOs;
using BeeLogistics.Modules.Map.Application.Interfaces;
using BeeLogistics.Modules.Map.Application.Services;
using BeeLogistics.Modules.Map.Domain;
using BeeLogistics.Modules.Map.Infrastructure.Services;
using BeeLogistics.Shared.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Map.Application.Handlers;

// Commands
public record UpdateDriverLocationCommand(Guid DriverId, UpdateLocationDto Dto, bool IsHistorical = false) : IRequest<Result<LocationDto>>;

// Queries
public record GetDriverLocationQuery(Guid DriverId) : IRequest<Result<LocationDto>>;
/// <param name="Limit">Maximum points to return; clamped by the repository. Optional and trailing, so existing callers are unaffected.</param>
public record GetDriverLocationHistoryQuery(Guid DriverId, DateTime? From = null, DateTime? To = null, int Limit = 500) : IRequest<Result<IReadOnlyList<LocationHistoryDto>>>;
public record GetLocationsWithinRadiusQuery(decimal Latitude, decimal Longitude, decimal RadiusKm) : IRequest<Result<IReadOnlyList<LocationDto>>>;

// Handlers
public class UpdateDriverLocationCommandHandler : IRequestHandler<UpdateDriverLocationCommand, Result<LocationDto>>
{
    // ILocationIngestService, not IPublishEndpoint. This handler used to inject the scoped
    // IPublishEndpoint, which - with UseBusOutbox() active on BookingsDbContext - staged the event
    // onto a context this request never saved, so nothing ever reached RabbitMQ (GitLab #37).
    // Routing through the ingest service also gives HTTP the same sanitization MQTT has always had.
    private readonly ILocationIngestService _ingestService;
    private readonly ILogger<UpdateDriverLocationCommandHandler> _logger;

    public UpdateDriverLocationCommandHandler(ILocationIngestService ingestService, ILogger<UpdateDriverLocationCommandHandler> logger)
    {
        _ingestService = ingestService;
        _logger = logger;
    }

    public async Task<Result<LocationDto>> Handle(UpdateDriverLocationCommand request, CancellationToken ct)
    {
        var dto = request.Dto;
        var driverId = request.DriverId;

        // Trust the device's own reading of when the fix was taken; fall back to now only when the
        // client did not send one. For a live update the two are the same, but for a buffered point
        // they can be far apart, and stamping the receive time destroys the trail.
        var timestamp = dto.RecordedAt ?? DateTime.UtcNow;

        var ingestResult = await _ingestService.IngestAsync(
            driverId,
            new DriverLocationUpdateDto
            {
                DriverId = driverId,
                Latitude = dto.Latitude,
                Longitude = dto.Longitude,
                Speed = dto.Speed,
                Heading = dto.Heading,
                Accuracy = dto.Accuracy,
                Timestamp = timestamp,
                DeviceId = dto.DeviceId
            },
            LocationSource.Http,
            request.IsHistorical,
            ct);

        if (!ingestResult.Accepted)
        {
            // Report the rejection instead of returning Ok regardless, which is what this handler
            // used to do. The driver app clears its buffer on a success response, so answering
            // "fine" to a point we dropped is how the data went missing without a trace.
            _logger.LogWarning("[LOCATION] [HTTP] Location rejected for driver {DriverId}: {Reason}", driverId, ingestResult.RejectionReason);
            return Result.Fail<LocationDto>(ingestResult.RejectionReason ?? "Location update rejected");
        }

        // Return simpler DTO without going to DB
        // The UI will likely ignore this anyway or just use it for ack
        var locationDto = new LocationDto(
            driverId,
            dto.Latitude,
            dto.Longitude,
            dto.Speed,
            dto.Heading,
            timestamp,
            dto.DeviceId
        );

        return Result.Ok(locationDto);
    }
}

public class GetDriverLocationQueryHandler : IRequestHandler<GetDriverLocationQuery, Result<LocationDto>>
{
    private readonly ILocationRepository _repository;
    private readonly IRedisLocationCache _redisCache;

    public GetDriverLocationQueryHandler(ILocationRepository repository, IRedisLocationCache redisCache)
    {
        _repository = repository;
        _redisCache = redisCache;
    }

    public async Task<Result<LocationDto>> Handle(GetDriverLocationQuery request, CancellationToken ct)
    {
        // 1. Try Redis first (fast path)
        var redisLoc = await _redisCache.GetDriverLocationAsync(request.DriverId, ct);
        
        if (redisLoc.HasValue)
        {
            var r = redisLoc.Value;
            var redisDto = new LocationDto(
                request.DriverId,
                r.Latitude,
                r.Longitude,
                r.Speed,
                r.Heading,
                r.Timestamp,
                r.DeviceId
            );
            return Result.Ok(redisDto);
        }

        // 2. Fallback to SQL (reliable path)
        var location = await _repository.GetCurrentLocationByDriverIdAsync(request.DriverId, ct);
        
        if (location == null)
            return Result.NotFound<LocationDto>("Location not found for driver");

        var dto = new LocationDto(
            location.DriverId,
            location.Latitude,
            location.Longitude,
            location.Speed,
            location.Heading,
            location.Timestamp,
            location.DeviceId
        );

        return Result.Ok(dto);
    }
}

public class GetDriverLocationHistoryQueryHandler : IRequestHandler<GetDriverLocationHistoryQuery, Result<IReadOnlyList<LocationHistoryDto>>>
{
    private readonly ILocationRepository _repository;

    public GetDriverLocationHistoryQueryHandler(ILocationRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<LocationHistoryDto>>> Handle(GetDriverLocationHistoryQuery request, CancellationToken ct)
    {
        var locations = await _repository.GetLocationHistoryByDriverIdAsync(
            request.DriverId,
            request.From,
            request.To,
            request.Limit,
            ct
        );

        var dtos = locations.Select(l => new LocationHistoryDto(
            l.Id,
            l.DriverId,
            l.Latitude,
            l.Longitude,
            l.Speed,
            l.Heading,
            l.Timestamp,
            l.DeviceId
        )).ToList();

        return Result.Ok<IReadOnlyList<LocationHistoryDto>>(dtos);
    }
}

public class GetLocationsWithinRadiusQueryHandler : IRequestHandler<GetLocationsWithinRadiusQuery, Result<IReadOnlyList<LocationDto>>>
{
    private readonly ILocationRepository _repository;

    public GetLocationsWithinRadiusQueryHandler(ILocationRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<IReadOnlyList<LocationDto>>> Handle(GetLocationsWithinRadiusQuery request, CancellationToken ct)
    {
        var locations = await _repository.GetLocationsWithinRadiusAsync(
            request.Latitude,
            request.Longitude,
            request.RadiusKm,
            ct
        );

        var dtos = locations.Select(l => new LocationDto(
            l.DriverId,
            l.Latitude,
            l.Longitude,
            l.Speed,
            l.Heading,
            l.Timestamp,
            l.DeviceId
        )).ToList();

        return Result.Ok<IReadOnlyList<LocationDto>>(dtos);
    }
}
