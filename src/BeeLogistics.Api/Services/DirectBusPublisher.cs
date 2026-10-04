using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Publishes notification messages straight to RabbitMQ via <see cref="IBus"/>,
/// bypassing the BookingsDbContext bus outbox. Same pattern as payment webhooks.
/// </summary>
public class DirectBusPublisher : IDirectBusPublisher
{
    private readonly IBus _bus;
    private readonly ILogger<DirectBusPublisher> _logger;

    public DirectBusPublisher(IBus bus, ILogger<DirectBusPublisher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        _logger.LogInformation("[NOTIF-TRACE] DirectBusPublisher publishing {MessageType} via IBus", typeof(T).Name);
        await _bus.Publish(message, cancellationToken);
        _logger.LogInformation("[NOTIF-TRACE] DirectBusPublisher published {MessageType} OK", typeof(T).Name);
    }
}
