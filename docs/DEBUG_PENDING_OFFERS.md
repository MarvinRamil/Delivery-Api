# Debug: Why Pending Offers Are Not Visible

## Quick Debug Checklist

### Step 1: Check if Offers Were Created

**SQL Query:**
```sql
-- Check if offers exist for a specific booking
SELECT 
    o."Id",
    o."BookingId",
    o."DriverId",
    o."Status",
    o."ExpiresAt",
    o."OfferedAt",
    o."SequenceNumber",
    b."BookingNumber",
    b."AssignmentStatus",
    b."SelectedDriverId",
    b."Status" as "BookingStatus"
FROM sales."DriverBookingOffers" o
JOIN sales."Bookings" b ON o."BookingId" = b."Id"
WHERE b."Id" = 'your-booking-id'
ORDER BY o."OfferedAt" DESC;
```

**What to Check:**
- ✅ Do offers exist? (If empty, consumer didn't create them)
- ✅ What is the `Status`? (Should be `Pending`)
- ✅ Is `ExpiresAt` in the future? (If expired, offers won't show)
- ✅ What is `DriverId`? (Must match the driver's user ID)

---

### Step 2: Check Driver ID Match

**Problem:** Driver ID in token doesn't match `DriverId` in offers

**Check:**
1. Get driver's user ID from token (decode JWT or check `sub` claim)
2. Compare with `DriverId` in `DriverBookingOffers` table

**SQL Query:**
```sql
-- Check offers for a specific driver
SELECT 
    o."Id",
    o."BookingId",
    o."DriverId",
    o."Status",
    o."ExpiresAt",
    o."OfferedAt",
    u."Email" as "DriverEmail",
    u."FullName" as "DriverName"
FROM sales."DriverBookingOffers" o
LEFT JOIN "AspNetUsers" u ON o."DriverId"::text = u."Id"
WHERE o."DriverId" = 'your-driver-user-id'
  AND o."Status" = 'Pending'
  AND o."ExpiresAt" > NOW()
ORDER BY o."OfferedAt" DESC;
```

---

### Step 3: Check Booking Status

**Problem:** Booking is filtered out by handler

**SQL Query:**
```sql
-- Check booking status
SELECT 
    "Id",
    "BookingNumber",
    "Status",
    "AssignmentStatus",
    "SelectedDriverId",
    "VehicleType",
    "CreatedAt",
    "UpdatedAt"
FROM sales."Bookings"
WHERE "Id" = 'your-booking-id';
```

**What to Check:**
- ✅ `SelectedDriverId` must be `NULL` (if set, offer is filtered out)
- ✅ `Status` must NOT be `Cancelled` or `Completed`
- ✅ `AssignmentStatus` should be `BroadcastingToDrivers`

---

### Step 4: Check if Consumer Ran

**Check Application Logs for:**
```
[INFO] Starting broadcast process for booking {BookingId}
[INFO] Found {Count} available drivers for booking {BookingId}
[INFO] Broadcast started for booking {BookingId} with {DriverCount} drivers
```

**OR**

```
[WARN] No drivers found for booking {BookingId}
[WARN] Booking {BookingId} not found during broadcast
[WARN] Booking {BookingId} is already being broadcasted
```

**If you don't see ANY logs:**
- RabbitMQ is not running or not connected
- Consumer is not registered
- Event is not being published

---

### Step 5: Check Driver Availability

**Problem:** No drivers found, so no offers created

**SQL Query:**
```sql
-- Check if there are any active drivers
SELECT 
    "Id",
    "Email",
    "FullName",
    "Role",
    "IsActive",
    "CurrentLatitude",
    "CurrentLongitude"
FROM "AspNetUsers"
WHERE "Role" = 'Driver' 
  AND "IsActive" = true;
```

**Requirements for Drivers:**
- ✅ `Role` = `'Driver'`
- ✅ `IsActive` = `true`
- ✅ (Optional) `CurrentLatitude` and `CurrentLongitude` for distance calculation

---

### Step 6: Check Offer Expiration

**Problem:** Offers expired before driver checked

**SQL Query:**
```sql
-- Check expired offers
SELECT 
    o."Id",
    o."BookingId",
    o."DriverId",
    o."Status",
    o."ExpiresAt",
    o."OfferedAt",
    NOW() as "CurrentTime",
    (o."ExpiresAt" - NOW()) as "TimeUntilExpiry"
FROM sales."DriverBookingOffers" o
WHERE o."BookingId" = 'your-booking-id'
  AND o."Status" = 'Pending'
ORDER BY o."OfferedAt" DESC;
```

**What to Check:**
- ✅ `ExpiresAt` > `NOW()` (offers expire after 2-5 minutes)
- ✅ If expired, Hangfire job should mark them as `Expired`

---

## Common Issues & Solutions

### Issue 1: No Offers Created

**Symptom:** `DriverBookingOffers` table is empty for the booking

**Possible Causes:**
1. No drivers found (check logs: `"No drivers found for booking"`)
2. Consumer didn't run (RabbitMQ issue)
3. Consumer failed silently (check logs for errors)

**Solution:**
- Check application logs
- Verify drivers exist and are active
- Verify RabbitMQ is running
- Check if consumer is registered

---

### Issue 2: Offers Created But Not Visible

**Symptom:** Offers exist in database but API returns empty array

**Possible Causes:**
1. **Driver ID mismatch** - Token user ID ≠ offer `DriverId`
2. **Offers expired** - `ExpiresAt` < `NOW()`
3. **Booking already assigned** - `SelectedDriverId` has value
4. **Booking cancelled/completed** - `Status` is `Cancelled` or `Completed`

**Solution:**
- Verify driver ID matches
- Check offer expiration times
- Check booking status
- Check `SelectedDriverId` is NULL

---

### Issue 3: Consumer Not Running

**Symptom:** Status is `BroadcastingToDrivers` but no offers created

**Possible Causes:**
1. RabbitMQ not running
2. Consumer not registered
3. Event not being consumed
4. Consumer failing silently

**Solution:**
- Check RabbitMQ status
- Check application logs
- Verify consumer registration in `Program.cs`
- Check MassTransit outbox for pending messages

---

## SQL Debugging Queries

### Complete Offer Check
```sql
-- Comprehensive check for a specific booking
SELECT 
    b."Id" as "BookingId",
    b."BookingNumber",
    b."Status" as "BookingStatus",
    b."AssignmentStatus",
    b."SelectedDriverId",
    b."VehicleType",
    COUNT(o."Id") as "TotalOffers",
    COUNT(CASE WHEN o."Status" = 'Pending' THEN 1 END) as "PendingOffers",
    COUNT(CASE WHEN o."Status" = 'Pending' AND o."ExpiresAt" > NOW() THEN 1 END) as "ActivePendingOffers",
    COUNT(CASE WHEN o."Status" = 'Accepted' THEN 1 END) as "AcceptedOffers",
    COUNT(CASE WHEN o."Status" = 'Rejected' THEN 1 END) as "RejectedOffers",
    COUNT(CASE WHEN o."Status" = 'Expired' THEN 1 END) as "ExpiredOffers"
FROM sales."Bookings" b
LEFT JOIN sales."DriverBookingOffers" o ON b."Id" = o."BookingId"
WHERE b."Id" = 'your-booking-id'
GROUP BY b."Id", b."BookingNumber", b."Status", b."AssignmentStatus", b."SelectedDriverId", b."VehicleType";
```

### Check Offers for Specific Driver
```sql
-- Check all offers for a driver
SELECT 
    o."Id" as "OfferId",
    o."BookingId",
    o."Status" as "OfferStatus",
    o."ExpiresAt",
    o."OfferedAt",
    b."BookingNumber",
    b."Status" as "BookingStatus",
    b."AssignmentStatus",
    b."SelectedDriverId",
    CASE 
        WHEN o."ExpiresAt" < NOW() THEN 'Expired'
        WHEN b."SelectedDriverId" IS NOT NULL THEN 'Booking Assigned'
        WHEN b."Status" IN ('Cancelled', 'Completed') THEN 'Booking Inactive'
        ELSE 'Should Be Visible'
    END as "VisibilityStatus"
FROM sales."DriverBookingOffers" o
JOIN sales."Bookings" b ON o."BookingId" = b."Id"
WHERE o."DriverId" = 'your-driver-user-id'
  AND o."Status" = 'Pending'
ORDER BY o."OfferedAt" DESC;
```

### Check All Broadcasting Bookings
```sql
-- See all bookings that are broadcasting
SELECT 
    b."Id",
    b."BookingNumber",
    b."AssignmentStatus",
    b."Status",
    b."SelectedDriverId",
    b."VehicleType",
    COUNT(o."Id") as "TotalOffers",
    COUNT(CASE WHEN o."Status" = 'Pending' AND o."ExpiresAt" > NOW() THEN 1 END) as "ActiveOffers"
FROM sales."Bookings" b
LEFT JOIN sales."DriverBookingOffers" o ON b."Id" = o."BookingId"
WHERE b."AssignmentStatus" = 'BroadcastingToDrivers'
GROUP BY b."Id", b."BookingNumber", b."AssignmentStatus", b."Status", b."SelectedDriverId", b."VehicleType"
ORDER BY b."UpdatedAt" DESC;
```

---

## Application Logs to Check

Look for these log messages in your application logs:

**Success:**
```
[INFO] Starting broadcast process for booking {BookingId}
[INFO] Found {Count} available drivers for booking {BookingId}
[INFO] Broadcast started for booking {BookingId} with {DriverCount} drivers
```

**Failure:**
```
[WARN] No drivers found for booking {BookingId}
[WARN] Booking {BookingId} not found during broadcast
[WARN] Booking {BookingId} is already being broadcasted
[ERROR] Error processing booking broadcast...
```

---

## Quick Fixes

### Fix 1: Verify Driver ID
Make sure the driver's user ID from the JWT token matches the `DriverId` in offers.

**Check Token:**
- Decode JWT at jwt.io
- Look for `sub` or `NameIdentifier` claim
- Compare with `DriverId` in database

### Fix 2: Check Offer Expiration
Offers expire after 2-5 minutes. If they expired, create new ones by triggering broadcast again.

### Fix 3: Verify Consumer Ran
Check application logs to see if the consumer processed the event. If not, check RabbitMQ.

### Fix 4: Check Booking Status
Ensure:
- `SelectedDriverId` = NULL
- `Status` ≠ `Cancelled` or `Completed`
- `AssignmentStatus` = `BroadcastingToDrivers`

---

## Next Steps

1. Run the SQL queries above to check database state
2. Check application logs for consumer activity
3. Verify driver ID matches
4. Check if offers are expired
5. Verify RabbitMQ is running

