using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Fraud.Application.Consumers;

public class OrderCreatedFraudConsumer : IConsumer<OrderCreatedEvent>
{
    private readonly IFraudEventRepository _eventRepo;
    private readonly IFraudSignalRepository _signalRepo;
    private readonly FraudOptions _options;
    private readonly ILogger<OrderCreatedFraudConsumer> _logger;

    public OrderCreatedFraudConsumer(
        IFraudEventRepository eventRepo,
        IFraudSignalRepository signalRepo,
        IOptions<FraudOptions> options,
        ILogger<OrderCreatedFraudConsumer> logger)
    {
        _eventRepo = eventRepo;
        _signalRepo = signalRepo;
        _options = options.Value;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var ct = context.CancellationToken;
        var msg = context.Message;
        var evt = FraudEvent.OrderCreated(msg.BookingId, msg.CustomerId, msg.CreatedAt);
        _eventRepo.Add(evt);

        var windowStart = msg.CreatedAt.AddMinutes(-_options.CustomerOrdersWindowMinutes);
        var count = await _eventRepo.CountOrderCreatedByCustomerAsync(msg.CustomerId, windowStart, msg.CreatedAt, ct);
        if (count >= _options.CustomerOrdersThreshold)
        {
            var signal = FraudSignal.Create(FraudRuleName.CustomerTooManyOrders, msg.CustomerId,
                $"{{\"count\":{count},\"windowMinutes\":{_options.CustomerOrdersWindowMinutes},\"bookingId\":\"{msg.BookingId}\"}}");
            _signalRepo.Add(signal);
            _logger.LogWarning("Fraud signal: Customer {CustomerId} created {Count} orders in {Minutes} min", msg.CustomerId, count, _options.CustomerOrdersWindowMinutes);
        }

        await _eventRepo.SaveChangesAsync(ct);
        if (count >= _options.CustomerOrdersThreshold)
            await _signalRepo.SaveChangesAsync(ct);
    }
}
