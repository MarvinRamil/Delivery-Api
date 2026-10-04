using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Fraud.Application.Consumers;

public class BookingCompletedFraudConsumer : IConsumer<BookingCompletedEvent>
{
    private readonly IFraudEventRepository _eventRepo;
    private readonly IFraudSignalRepository _signalRepo;
    private readonly FraudOptions _options;
    private readonly ILogger<BookingCompletedFraudConsumer> _logger;

    public BookingCompletedFraudConsumer(
        IFraudEventRepository eventRepo,
        IFraudSignalRepository signalRepo,
        IOptions<FraudOptions> options,
        ILogger<BookingCompletedFraudConsumer> logger)
    {
        _eventRepo = eventRepo;
        _signalRepo = signalRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingCompletedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var evt = FraudEvent.DeliveryCompleted(msg.BookingId, msg.DriverId, msg.CustomerId, msg.CompletedAt);
        _eventRepo.Add(evt);

        var windowStart = msg.CompletedAt.AddMinutes(-_options.DriverDeliveriesWindowMinutes);
        var count = await _eventRepo.CountDeliveryCompletedByDriverAsync(msg.DriverId, windowStart, msg.CompletedAt, ct);
        if (count >= _options.DriverDeliveriesThreshold)
        {
            var signal = FraudSignal.Create(FraudRuleName.DriverTooManyDeliveries, msg.DriverId,
                $"{{\"count\":{count},\"windowMinutes\":{_options.DriverDeliveriesWindowMinutes},\"bookingId\":\"{msg.BookingId}\"}}");
            _signalRepo.Add(signal);
            _logger.LogWarning("Fraud signal: Driver {DriverId} completed {Count} deliveries in {Minutes} min", msg.DriverId, count, _options.DriverDeliveriesWindowMinutes);
        }

        await _eventRepo.SaveChangesAsync(ct);
        if (count >= _options.DriverDeliveriesThreshold)
            await _signalRepo.SaveChangesAsync(ct);
    }
}
