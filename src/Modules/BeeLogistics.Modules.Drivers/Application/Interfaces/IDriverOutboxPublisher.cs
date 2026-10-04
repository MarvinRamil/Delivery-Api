using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Drivers.Infrastructure;
using System.Text.Json;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

/// <summary>
/// Publishes events to the transactional outbox. The message is added to the
/// DriversDbContext change tracker so it's committed atomically with the
/// business data when SaveChangesAsync is called.
/// 
/// IMPORTANT: Call Publish() BEFORE the SaveChanges that commits the business data.
/// The outbox message will be persisted in the same transaction.
/// </summary>
public interface IDriverOutboxPublisher
{
    /// <summary>
    /// Adds an outbox message to the DbContext tracker (no SaveChanges).
    /// Must be called before the handler's final SaveChangesAsync.
    /// </summary>
    void Publish<TEvent>(TEvent @event) where TEvent : class;
}

public class DriverOutboxPublisher : IDriverOutboxPublisher
{
    private readonly DriversDbContext _dbContext;

    public DriverOutboxPublisher(DriversDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public void Publish<TEvent>(TEvent @event) where TEvent : class
    {
        var eventType = typeof(TEvent).FullName ?? typeof(TEvent).Name;
        var payload = JsonSerializer.Serialize(@event);
        var message = new OutboxMessage(eventType, payload);
        _dbContext.OutboxMessages.Add(message);
        // No SaveChanges — the caller's SaveChanges commits this atomically
    }
}
