namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Publishes a message directly to RabbitMQ (bypassing the BookingsDbContext bus
/// outbox). Used by notification push/dispatch endpoints — payment webhooks inject
/// <c>IBus</c> directly for the same reason.
/// </summary>
public interface IDirectBusPublisher
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) where T : class;
}
