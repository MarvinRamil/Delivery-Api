using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure;
using System.Text.Json;

namespace BeeLogistics.Modules.Payment.Application.Interfaces;

/// <summary>
/// Publishes events to the transactional outbox. The message is added to the
/// PaymentDbContext change tracker so it's committed atomically with the
/// business data when SaveChangesAsync is called.
///
/// IMPORTANT: Call Publish() BEFORE the SaveChanges that commits the business data.
/// The outbox message will be persisted in the same transaction.
/// </summary>
public interface IPaymentOutboxPublisher
{
    /// <summary>
    /// Adds an outbox message to the DbContext tracker (no SaveChanges).
    /// Must be called before the handler's final SaveChangesAsync.
    /// </summary>
    void Publish<TEvent>(TEvent @event) where TEvent : class;
}

public class PaymentOutboxPublisher : IPaymentOutboxPublisher
{
    private readonly PaymentDbContext _dbContext;

    public PaymentOutboxPublisher(PaymentDbContext dbContext)
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
