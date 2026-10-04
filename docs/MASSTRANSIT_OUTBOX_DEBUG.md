# MassTransit Outbox Debugging Guide

## Why You See Queues But No Messages

The system uses **MassTransit Outbox Pattern** - messages are NOT published directly to RabbitMQ. Instead:

1. Message is stored in database (`OutboxMessage` table)
2. Background service reads from database
3. Background service publishes to RabbitMQ
4. Message appears in RabbitMQ queue

**This means there's a delay (up to 10 seconds) between publishing and appearing in RabbitMQ.**

---

## How It Works

### Step 1: Message Published
```csharp
await _publishEndpoint.Publish(new BookingBroadcastRequested(bookingId));
```

**What Happens:**
- Message is stored in `sales.OutboxMessage` table
- NOT yet sent to RabbitMQ

### Step 2: Transaction Commits
```csharp
await _bookingRepository.SaveChangesAsync(ct);
```

**What Happens:**
- Database transaction commits
- Outbox message is now ready to be published

### Step 3: Outbox Service Publishes (Background)
- MassTransit outbox service runs in background
- Checks `OutboxMessage` table every **10 seconds** (QueryDelay)
- Publishes pending messages to RabbitMQ
- Updates `SentTime` in database

---

## Check Outbox Status

### SQL Query: Check Pending Messages

```sql
-- Check if messages are stuck in outbox
SELECT 
    "SequenceNumber",
    "MessageId",
    "EnqueueTime",
    "SentTime",
    "ExpirationTime",
    CASE 
        WHEN "SentTime" IS NULL THEN 'Pending'
        ELSE 'Sent'
    END as "Status",
    NOW() - "EnqueueTime" as "Age"
FROM sales."OutboxMessage"
WHERE "SentTime" IS NULL
ORDER BY "EnqueueTime" DESC
LIMIT 20;
```

**What to Check:**
- ✅ If `SentTime` is NULL → Message is pending (not yet published)
- ✅ If `Age` > 1 minute → Message might be stuck
- ✅ If no rows → All messages were published

---

### SQL Query: Check Recent Messages

```sql
-- Check messages sent in last hour
SELECT 
    "SequenceNumber",
    "MessageId",
    "EnqueueTime",
    "SentTime",
    "SentTime" - "EnqueueTime" as "ProcessingTime"
FROM sales."OutboxMessage"
WHERE "SentTime" > NOW() - INTERVAL '1 hour'
ORDER BY "SentTime" DESC;
```

---

## Troubleshooting

### Issue 1: Messages Stuck in Outbox

**Symptom:** Messages in `OutboxMessage` table with `SentTime = NULL` for > 1 minute

**Possible Causes:**
1. Outbox service not running
2. RabbitMQ connection issue
3. Message serialization error

**Solution:**
- Check application logs for outbox errors
- Verify RabbitMQ is running and accessible
- Check RabbitMQ connection settings

---

### Issue 2: Outbox Service Not Running

**Symptom:** No messages being published, outbox table has pending messages

**Check:**
- MassTransit outbox service runs automatically when application starts
- It's part of the MassTransit bus, not a separate service
- Check application logs for: "Publishing outbox message"

**Solution:**
- Restart application
- Check MassTransit configuration in `Program.cs`
- Verify outbox tables exist in database

---

### Issue 3: QueryDelay Too Long

**Symptom:** Messages take too long to appear in RabbitMQ

**Current Setting:**
```csharp
o.QueryDelay = TimeSpan.FromSeconds(10); // Checks every 10 seconds
```

**Solution:**
- Reduce `QueryDelay` to 5 seconds or less (more frequent checks)
- Trade-off: More database queries vs faster publishing

---

### Issue 4: Messages Not Being Consumed

**Symptom:** Messages in RabbitMQ queue but not being consumed

**Check:**
- Consumer is registered: `x.AddConsumer<BookingBroadcastConsumer>()`
- Consumer endpoint is configured: `cfg.ConfigureEndpoints(context)`
- Check application logs for consumer activity

**Solution:**
- Verify consumer registration in `Program.cs`
- Check application logs for consumer errors
- Verify RabbitMQ connection

---

## Check Outbox Tables

### Verify Tables Exist

```sql
-- Check if outbox tables exist
SELECT table_name 
FROM information_schema.tables 
WHERE table_schema = 'sales' 
  AND table_name IN ('OutboxState', 'OutboxMessage', 'InboxState');
```

**Expected:** 3 tables should exist

---

### Check Outbox State

```sql
-- Check outbox instances
SELECT 
    "OutboxId",
    "Created",
    "Delivered",
    "LastSequenceNumber"
FROM sales."OutboxState"
ORDER BY "Created" DESC
LIMIT 10;
```

---

## Application Logs to Check

Look for these log messages:

**Outbox Publishing:**
```
[INFO] Publishing outbox message {MessageId}
[INFO] Outbox message {MessageId} sent successfully
```

**Outbox Errors:**
```
[ERROR] Failed to publish outbox message {MessageId}
[ERROR] Outbox service error: ...
```

**Consumer Activity:**
```
[INFO] Starting broadcast process for booking {BookingId}
[INFO] Broadcast started for booking {BookingId} with {DriverCount} drivers
```

---

## Manual Check: Force Outbox Processing

The outbox service runs automatically, but you can verify it's working:

1. **Publish a message** (trigger broadcast)
2. **Wait 10-15 seconds** (QueryDelay)
3. **Check RabbitMQ** - message should appear
4. **Check database** - `SentTime` should be set

---

## Configuration

### Current Outbox Settings

```csharp
x.AddEntityFrameworkOutbox<SalesDbContext>(o =>
{
    o.QueryDelay = TimeSpan.FromSeconds(10); // Check every 10 seconds
    o.UsePostgres(); // PostgreSQL storage
    o.UseBusOutbox(); // Enable transactional publishing
});
```

### Reduce Delay (Faster Publishing)

```csharp
o.QueryDelay = TimeSpan.FromSeconds(5); // Check every 5 seconds
```

---

## Complete Diagnostic Query

```sql
-- Complete outbox diagnostic
SELECT 
    COUNT(*) FILTER (WHERE "SentTime" IS NULL) as "PendingMessages",
    COUNT(*) FILTER (WHERE "SentTime" IS NOT NULL) as "SentMessages",
    COUNT(*) as "TotalMessages",
    MAX("EnqueueTime") as "LastEnqueued",
    MAX("SentTime") as "LastSent",
    AVG(EXTRACT(EPOCH FROM ("SentTime" - "EnqueueTime"))) as "AvgProcessingSeconds"
FROM sales."OutboxMessage"
WHERE "EnqueueTime" > NOW() - INTERVAL '1 hour';
```

---

## Quick Fixes

### Fix 1: Restart Application
If outbox service is stuck, restart the application.

### Fix 2: Check RabbitMQ Connection
Verify RabbitMQ is running and accessible:
```bash
# Check RabbitMQ status
docker ps | grep rabbitmq

# Check RabbitMQ logs
docker logs <rabbitmq-container>
```

### Fix 3: Reduce QueryDelay
If messages are taking too long, reduce `QueryDelay` in `Program.cs`.

### Fix 4: Check Consumer Registration
Verify consumer is registered:
```csharp
x.AddConsumer<BookingBroadcastConsumer>();
```

---

## Expected Flow

```
1. POST /api/bookings/{id}/start-broadcast
   ↓
2. Handler publishes event
   ↓
3. Message stored in OutboxMessage (SentTime = NULL)
   ↓
4. Transaction commits
   ↓
5. Wait up to 10 seconds (QueryDelay)
   ↓
6. Outbox service reads OutboxMessage
   ↓
7. Message published to RabbitMQ
   ↓
8. SentTime updated in database
   ↓
9. Consumer receives message
   ↓
10. Offers created
```

---

## Next Steps

1. ✅ Run SQL queries to check outbox status
2. ✅ Check application logs for outbox activity
3. ✅ Verify RabbitMQ is running
4. ✅ Wait 10-15 seconds after publishing
5. ✅ Check if messages appear in RabbitMQ

