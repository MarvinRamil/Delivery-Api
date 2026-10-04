# MassTransit Outbox Pattern Guide

## Overview

The MassTransit outbox pattern ensures **exactly-once message delivery** by storing messages in the database within the same transaction as your business data. Messages are only published to RabbitMQ after the database transaction commits successfully.

## Why Use Outbox?

### Problem Without Outbox
1. **Scenario**: Publish message → Save to database
   - If database save fails, message is already published (inconsistent state)
2. **Scenario**: Save to database → Publish message
   - If publish fails, database is saved but downstream services don't get notified

### Solution With Outbox
1. Publish message (stored in outbox table)
2. Save business data to database
3. Commit transaction (both outbox and business data)
4. Background service publishes outbox messages to RabbitMQ
5. **Result**: Messages only published if transaction succeeds

## Implementation

### 1. ✅ Package Installation
- Added `MassTransit.EntityFrameworkCore` package to API project

### 2. ✅ Configuration (Program.cs)

```csharp
builder.Services.AddMassTransit(x =>
{
    // Register consumers...
    
    // Configure Entity Framework Core Outbox
    x.AddEntityFrameworkOutbox<SalesDbContext>(o =>
    {
        o.QueryDelay = TimeSpan.FromSeconds(10); // How often to check for pending messages
        o.UsePostgres(); // Use PostgreSQL for outbox storage
        o.UseBusOutbox(); // Enable transactional publishing
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        // RabbitMQ configuration...
    });
});
```

### 3. ✅ Database Tables

The migration `20250127000001_AddMassTransitOutbox` creates three tables in the `sales` schema:

- **OutboxState**: Tracks outbox instances
- **OutboxMessage**: Stores messages to be published
- **InboxState**: Prevents duplicate message processing (idempotency)

## How It Works

### Publishing Messages with Outbox

When you publish a message within a request that uses `SalesDbContext`:

```csharp
public class StartBroadcastingBookingCommandHandler
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IBookingRepository _bookingRepository;
    private readonly SalesDbContext _context; // Same context as outbox

    public async Task<Result> Handle(...)
    {
        // 1. Publish message (stored in OutboxMessage table)
        await _publishEndpoint.Publish(new BookingBroadcastRequested(bookingId));
        
        // 2. Save business data
        await _bookingRepository.SaveChangesAsync();
        
        // 3. Transaction commits → Outbox service publishes message to RabbitMQ
    }
}
```

### Message Flow

```
1. Application publishes message
   ↓
2. Message stored in OutboxMessage table (same transaction)
   ↓
3. Business data saved to database
   ↓
4. Transaction commits
   ↓
5. Outbox service (background) reads OutboxMessage
   ↓
6. Message published to RabbitMQ
   ↓
7. OutboxMessage marked as sent
```

## Best Practices

### 1. Use Same DbContext
The outbox must use the same `DbContext` as your business logic:

```csharp
// ✅ Good: Same context
services.AddDbContext<SalesDbContext>(...);
x.AddEntityFrameworkOutbox<SalesDbContext>(...);

// ❌ Bad: Different context
services.AddDbContext<SalesDbContext>(...);
x.AddEntityFrameworkOutbox<OtherDbContext>(...); // Won't work!
```

### 2. Publish Before SaveChanges
Always publish messages before calling `SaveChangesAsync()`:

```csharp
// ✅ Good
await _publishEndpoint.Publish(event);
await _repository.SaveChangesAsync();

// ❌ Bad (message might not be in transaction)
await _repository.SaveChangesAsync();
await _publishEndpoint.Publish(event);
```

### 3. Use Scoped Services
Ensure `IPublishEndpoint` and `DbContext` are in the same scope:

```csharp
// ✅ Good: Both scoped
services.AddScoped<IPublishEndpoint>(...);
services.AddDbContext<SalesDbContext>(...);

// ❌ Bad: Singleton endpoint with scoped context
services.AddSingleton<IPublishEndpoint>(...);
```

## Monitoring

### Check Outbox Status

```sql
-- Pending messages
SELECT COUNT(*) FROM sales."OutboxMessage" 
WHERE "SentTime" IS NULL;

-- Messages sent in last hour
SELECT COUNT(*) FROM sales."OutboxMessage"
WHERE "SentTime" > NOW() - INTERVAL '1 hour';

-- Stuck messages (older than 5 minutes)
SELECT * FROM sales."OutboxMessage"
WHERE "SentTime" IS NULL 
  AND "EnqueueTime" < NOW() - INTERVAL '5 minutes';
```

### Outbox Service Logs

The outbox service logs when it processes messages:
- Look for: "Publishing outbox message" in application logs
- Check RabbitMQ management UI for message delivery

## Troubleshooting

### Messages Not Publishing

1. **Check Outbox Service**: Ensure it's running (part of MassTransit)
2. **Check QueryDelay**: Default is 10 seconds - messages may take up to 10s to publish
3. **Check Database**: Verify outbox tables exist and have data
4. **Check Logs**: Look for outbox-related errors

### Duplicate Messages

The outbox ensures exactly-once delivery, but if you see duplicates:
1. Check `InboxState` table for consumer idempotency
2. Verify consumers handle idempotency correctly
3. Check for multiple outbox services running

### Performance

- **QueryDelay**: Lower = faster publishing but more database queries
- **Batch Size**: Outbox processes messages in batches (configurable)
- **Indexes**: Migration creates indexes for optimal performance

## Migration Details

### Tables Created

**OutboxState** (sales schema):
- `OutboxId` (PK): Unique identifier for outbox instance
- `Created`: When outbox was created
- `Delivered`: When all messages were delivered
- `LastSequenceNumber`: Last message sequence processed

**OutboxMessage** (sales schema):
- `SequenceNumber` (PK): Auto-incrementing sequence
- `MessageId`: Unique message identifier
- `Body`: Serialized message content
- `SentTime`: When message was published to RabbitMQ
- `EnqueueTime`: When message was queued

**InboxState** (sales schema):
- Prevents duplicate message processing
- Used by consumers for idempotency

## Next Steps

1. ✅ Run migration: `dotnet ef database update --context SalesDbContext`
2. ✅ Test message publishing with outbox
3. ⏳ Monitor outbox performance in production
4. ⏳ Consider adding outbox to other modules if needed

## References

- [MassTransit Outbox Documentation](https://masstransit.io/documentation/patterns/outbox)
- [Entity Framework Core Outbox](https://masstransit.io/documentation/patterns/outbox#entity-framework-core)
