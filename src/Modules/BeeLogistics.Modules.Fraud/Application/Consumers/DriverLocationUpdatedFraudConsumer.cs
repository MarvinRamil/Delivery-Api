using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using BeeLogistics.Modules.Map.Application.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Fraud.Application.Consumers;

public class DriverLocationUpdatedFraudConsumer : IConsumer<DriverLocationUpdatedEvent>
{
    private readonly IFraudEventRepository _eventRepo;
    private readonly IFraudSignalRepository _signalRepo;
    private readonly FraudOptions _options;
    private readonly ILogger<DriverLocationUpdatedFraudConsumer> _logger;

    public DriverLocationUpdatedFraudConsumer(
        IFraudEventRepository eventRepo,
        IFraudSignalRepository signalRepo,
        IOptions<FraudOptions> options,
        ILogger<DriverLocationUpdatedFraudConsumer> logger)
    {
        _eventRepo = eventRepo;
        _signalRepo = signalRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<DriverLocationUpdatedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        decimal? prevLat = null, prevLng = null, distanceKm = null;

        var prev = await _eventRepo.GetLatestLocationByDriverAsync(msg.DriverId, ct);
        if (prev != null)
        {
            prevLat = prev.Latitude;
            prevLng = prev.Longitude;
            distanceKm = (decimal)Haversine.DistanceKm((double)prev.Latitude, (double)prev.Longitude, (double)msg.Latitude, (double)msg.Longitude);
        }

        var evt = FraudEvent.LocationUpdated(msg.DriverId, msg.Timestamp, msg.Latitude, msg.Longitude, prevLat, prevLng, distanceKm, msg.DeviceId);
        _eventRepo.Add(evt);

        if (distanceKm.HasValue && distanceKm.Value >= (decimal)_options.GpsJumpKmThreshold)
        {
            var signal = FraudSignal.Create(FraudRuleName.GpsImpossibleJump, msg.DriverId,
                $"{{\"distanceKm\":{distanceKm.Value},\"thresholdKm\":{_options.GpsJumpKmThreshold},\"lat\":{msg.Latitude},\"lng\":{msg.Longitude}}}");
            _signalRepo.Add(signal);
            _logger.LogWarning("Fraud signal: Driver {DriverId} GPS jump {DistanceKm} km", msg.DriverId, distanceKm.Value);
        }

        await _eventRepo.SaveChangesAsync(ct);
        if (distanceKm.HasValue && distanceKm.Value >= (decimal)_options.GpsJumpKmThreshold)
            await _signalRepo.SaveChangesAsync(ct);
    }
}
