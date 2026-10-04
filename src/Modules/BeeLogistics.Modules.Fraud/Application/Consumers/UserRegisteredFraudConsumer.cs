using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Fraud.Application.Consumers;

public class UserRegisteredFraudConsumer : IConsumer<UserRegisteredEvent>
{
    private readonly IFraudEventRepository _eventRepo;
    private readonly IFraudSignalRepository _signalRepo;
    private readonly FraudOptions _options;
    private readonly ILogger<UserRegisteredFraudConsumer> _logger;

    public UserRegisteredFraudConsumer(
        IFraudEventRepository eventRepo,
        IFraudSignalRepository signalRepo,
        IOptions<FraudOptions> options,
        ILogger<UserRegisteredFraudConsumer> logger)
    {
        _eventRepo = eventRepo;
        _signalRepo = signalRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserRegisteredEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var deviceId = msg.DeviceId ?? msg.DeviceFingerprint ?? "unknown";
        var evt = FraudEvent.UserRegistered(msg.UserId, msg.RegisteredAt, deviceId);
        _eventRepo.Add(evt);
        await _eventRepo.SaveChangesAsync(ct);

        if (deviceId != "unknown")
        {
            var userCount = await _eventRepo.CountDistinctUsersByDeviceAsync(deviceId, null, null, ct);
            if (userCount >= _options.DeviceUsersThreshold)
            {
                var signal = FraudSignal.Create(FraudRuleName.DeviceTooManyUsers, msg.UserId,
                    $"{{\"deviceId\":\"{deviceId}\",\"userCount\":{userCount},\"threshold\":{_options.DeviceUsersThreshold}}}");
                _signalRepo.Add(signal);
                await _signalRepo.SaveChangesAsync(ct);
                _logger.LogWarning("Fraud signal: Device {DeviceId} has {UserCount} registered users", deviceId, userCount);
            }
        }
    }
}
