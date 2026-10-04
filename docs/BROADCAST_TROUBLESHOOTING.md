# Broadcast Troubleshooting Guide

## Why You Don't See BroadcastingToDrivers Status

The `AssignmentStatus = BroadcastingToDrivers` is set **asynchronously** by the `BookingBroadcastConsumer` after processing the event. There are several reasons why you might not see this status:

---

## Common Issues

### 1. RabbitMQ Not Running or Not Connected

**Symptom:** Event is published but never consumed

**Check:**
- Is RabbitMQ running? (Check Docker Compose or service status)
- Is RabbitMQ connection configured correctly in `appsettings.json`?
- Check application logs for RabbitMQ connection errors

**Solution:**
```bash
# Check RabbitMQ status
docker ps | grep rabbitmq

# Check RabbitMQ logs
docker logs <rabbitmq-container-name>

# Verify connection string in appsettings.json
"RabbitMq": {
  "Host": "localhost",
  "Port": "5672",
  "Username": "guest",
  "Password": "guest"
}
```

---

### 2. No Available Drivers Found

**Symptom:** Event is consumed but status never changes to `BroadcastingToDrivers`

**Why:** The consumer returns early if no drivers are found (line 63-68 in `BookingBroadcastConsumer.cs`)

**Check:**
```sql
-- Check if there are any drivers in the system
SELECT * FROM "AspNetUsers" WHERE "Role" = 'Driver';

-- Check if drivers are active/online
-- (This depends on your driver status tracking)

-- Check if drivers have matching vehicle types
SELECT * FROM "Trucks" WHERE "VehicleType" = 'Closed Van' OR "VehicleType" = 'L300';
```

**Solution:**
- Ensure drivers exist in the database
- Ensure drivers are marked as active/online
- Ensure drivers have vehicles with matching vehicle types
- Check application logs for: `"No drivers found for booking {BookingId}"`

---

### 3. Consumer Not Registered

**Symptom:** Event published but never processed

**Check:**
- Verify consumer is registered in `Program.cs`:
  ```csharp
  x.AddConsumer<BeeLogistics.Modules.Sales.Application.Consumers.BookingBroadcastConsumer>();
  ```
- Check application startup logs for consumer registration

---

### 4. Event Not Being Published

**Symptom:** API returns 200 OK but nothing happens

**Check:**
- Verify `StartBroadcastingBookingCommand` handler is called
- Check application logs for: `"Starting broadcast process for booking {BookingId}"`
- Check MassTransit outbox for pending messages

**SQL Query:**
```sql
-- Check MassTransit outbox for pending messages
SELECT * FROM sales."OutboxMessage" 
WHERE "EnqueueTime" > NOW() - INTERVAL '1 hour'
ORDER BY "EnqueueTime" DESC;
```

---

### 5. Consumer Failing Silently

**Symptom:** Event consumed but status not updated

**Check Application Logs:**
- Look for errors in `BookingBroadcastConsumer`
- Check for exceptions during driver availability service calls
- Check for database connection issues

**Common Errors:**
- `"Booking {BookingId} not found during broadcast"` - Booking was deleted
- `"No drivers found for booking {BookingId}"` - No matching drivers
- Database connection errors
- Driver availability service errors

---

## Debugging Steps

### Step 1: Check if Event is Published

**Check Application Logs:**
```
[INFO] Starting broadcast process for booking {BookingId}
```

If you don't see this log, the consumer is not processing the event.

### Step 2: Check if Drivers Are Found

**Check Application Logs:**
```
[INFO] Broadcast started for booking {BookingId} with {DriverCount} drivers
```

OR

```
[WARN] No drivers found for booking {BookingId}
```

### Step 3: Check Database Status

**SQL Query:**
```sql
-- Check booking assignment status
SELECT 
    "Id",
    "BookingNumber",
    "AssignmentStatus",
    "Status",
    "VehicleType",
    "CreatedAt",
    "UpdatedAt"
FROM sales."Bookings"
WHERE "Id" = 'your-booking-id';

-- Check if offers were created
SELECT 
    "Id",
    "BookingId",
    "DriverId",
    "Status",
    "ExpiresAt",
    "CreatedAt"
FROM sales."DriverBookingOffers"
WHERE "BookingId" = 'your-booking-id';
```

### Step 4: Check RabbitMQ

**RabbitMQ Management UI:**
- URL: `http://localhost:15672`
- Username: `guest`
- Password: `guest`

**Check:**
- Queues tab - Look for `BookingBroadcastRequested` queue
- Messages - Check if messages are being consumed
- Connections - Verify application is connected

---

## Quick Fix: Set Status Optimistically

If you want the status to be set immediately (before consumer processes), you can modify the handler to set it optimistically:

**Current Behavior:**
- Handler publishes event → Returns OK
- Consumer processes event → Sets status

**Modified Behavior:**
- Handler sets status → Publishes event → Returns OK
- Consumer processes event → Creates offers

**Code Change:**
```csharp
// In StartBroadcastingBookingCommandHandler
booking.StartBroadcastingToDrivers();
await _bookingRepository.SaveChangesAsync(ct);
await _publishEndpoint.Publish(new BookingBroadcastRequested(request.BookingId), ct);
```

**Note:** This sets the status immediately, but offers are still created asynchronously by the consumer.

---

## SQL Queries for Debugging

### Check Broadcast Status
```sql
SELECT 
    "Id",
    "BookingNumber",
    "AssignmentStatus",
    "Status",
    "VehicleType",
    "SelectedDriverId",
    "CreatedAt",
    "UpdatedAt"
FROM sales."Bookings"
WHERE "AssignmentStatus" = 'BroadcastingToDrivers'
ORDER BY "UpdatedAt" DESC;
```

### Check if Offers Were Created
```sql
SELECT 
    o."Id",
    o."BookingId",
    o."DriverId",
    o."Status",
    o."ExpiresAt",
    o."OfferedAt",
    b."BookingNumber",
    b."AssignmentStatus"
FROM sales."DriverBookingOffers" o
JOIN sales."Bookings" b ON o."BookingId" = b."Id"
WHERE b."Id" = 'your-booking-id'
ORDER BY o."OfferedAt" DESC;
```

### Check MassTransit Outbox
```sql
SELECT 
    "SequenceNumber",
    "EnqueueTime",
    "ExpirationTime",
    "Body"
FROM sales."OutboxMessage"
WHERE "EnqueueTime" > NOW() - INTERVAL '1 hour'
ORDER BY "EnqueueTime" DESC
LIMIT 10;
```

---

## Expected Flow

1. ✅ Call `POST /api/bookings/{id}/start-broadcast`
2. ✅ Handler publishes `BookingBroadcastRequested` event
3. ✅ Consumer receives event (check logs)
4. ✅ Consumer finds available drivers
5. ✅ Consumer creates offers
6. ✅ Consumer sets `AssignmentStatus = BroadcastingToDrivers`
7. ✅ Status saved to database

**If step 3 fails:** RabbitMQ issue
**If step 4 fails:** No drivers found (check logs)
**If step 6 fails:** Database save issue (check logs)

---

## Check Application Logs

Look for these log messages:

**Success:**
```
[INFO] Starting broadcast process for booking {BookingId}
[INFO] Broadcast started for booking {BookingId} with {DriverCount} drivers
```

**Failure:**
```
[WARN] Booking {BookingId} not found during broadcast
[WARN] Booking {BookingId} is already being broadcasted
[WARN] No drivers found for booking {BookingId}
```

---

## Next Steps

1. Check application logs for consumer activity
2. Verify RabbitMQ is running and connected
3. Check if drivers exist and are available
4. Run SQL queries to check database state
5. Consider setting status optimistically if needed

