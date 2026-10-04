# Driver Offers Filters and Conditions

This document explains all the filters and conditions that determine whether a driver will see booking offers.

## Endpoint
`GET /api/driver-offers/pending?limit=3`

## Filters Applied

### 1. Repository Level Filters (`GetPendingOffersForDriverAsync`)

These filters are applied at the database level:

- ✅ **DriverId must match** - Only offers for the authenticated driver
- ✅ **Status = Pending** - Only pending offers (not Accepted, Rejected, or Expired)
- ✅ **ExpiresAt > now** - Only non-expired offers (offers expire after 2-5 minutes)

**Code Location:** `DriverBookingOfferRepository.GetPendingOffersForDriverAsync()`

### 2. Handler Level Filters (`GetPendingOffersQueryHandler`)

After fetching offers, these additional filters are applied:

- ✅ **Booking must exist** - If booking is deleted, offer is skipped
- ✅ **SelectedDriverId must be NULL** - Booking must not be assigned to any driver
- ✅ **Booking.Status must NOT be Cancelled or Completed** - Only active bookings

**Code Location:** `DriverOfferHandlers.cs` → `GetPendingOffersQueryHandler.Handle()`

## Why You Might Get Empty Results

### Common Reasons:

1. **No offers exist for this driver**
   - Check: Does the driver have any `DriverBookingOffer` records in the database?
   - Solution: Create a booking and trigger the broadcast process

2. **All offers are expired**
   - Offers expire after 2-5 minutes
   - Check: `ExpiresAt` field in `DriverBookingOffer` table
   - Solution: Create new offers or extend expiration time

3. **All bookings are already assigned**
   - If `Booking.SelectedDriverId` has a value, the offer is filtered out
   - Check: `SelectedDriverId` field in `Bookings` table
   - Solution: Use unassigned bookings or reset `SelectedDriverId` to NULL

4. **All bookings are Cancelled or Completed**
   - Check: `Status` field in `Bookings` table
   - Solution: Use bookings with status `Pending` or `Confirmed`

5. **Driver ID mismatch**
   - The driver ID from the JWT token must match `DriverId` in `DriverBookingOffer`
   - Check: Token claims (`sub` or `NameIdentifier`) vs database `DriverId`
   - Solution: Ensure the logged-in user's ID matches the offer's `DriverId`

6. **Booking not in BroadcastingToDrivers status**
   - Offers are only created when booking is broadcasted
   - Check: `AssignmentStatus` should be `BroadcastingToDrivers`
   - Solution: Trigger booking broadcast via `StartBroadcastingBookingCommand`

## How Offers Are Created

Offers are created when:

1. **Booking is created** → Status: `Pending`
2. **Broadcast is triggered** → `BookingBroadcastRequested` event published
3. **BookingBroadcastConsumer processes event**:
   - Finds available drivers (active, matching vehicle type, sorted by distance)
   - Creates `DriverBookingOffer` for each driver
   - Sets `AssignmentStatus = BroadcastingToDrivers`
   - Sets expiration time (2-5 minutes)

**Code Location:** `BookingBroadcastConsumer.cs`

## Testing/Debugging

To see offers, ensure:

1. ✅ Booking exists with `Status = Pending` or `Confirmed`
2. ✅ Booking has `SelectedDriverId = NULL`
3. ✅ Booking has `AssignmentStatus = BroadcastingToDrivers`
4. ✅ `DriverBookingOffer` records exist with:
   - `DriverId` = your driver's user ID
   - `Status = Pending`
   - `ExpiresAt > DateTime.UtcNow`
5. ✅ Driver is logged in with JWT token containing correct user ID

## SQL Queries for Debugging

```sql
-- Check if offers exist for a driver
SELECT * FROM "DriverBookingOffers" 
WHERE "DriverId" = 'your-driver-id' 
  AND "Status" = 'Pending' 
  AND "ExpiresAt" > NOW();

-- Check booking status
SELECT "Id", "BookingNumber", "Status", "SelectedDriverId", "AssignmentStatus"
FROM "Bookings"
WHERE "Id" IN (SELECT "BookingId" FROM "DriverBookingOffers" WHERE "DriverId" = 'your-driver-id');

-- Check if offers are expired
SELECT COUNT(*) as expired_count
FROM "DriverBookingOffers"
WHERE "DriverId" = 'your-driver-id' 
  AND "Status" = 'Pending' 
  AND "ExpiresAt" <= NOW();
```

## Potential Issues

### Issue 1: Offers exist but handler filters them out
- **Symptom:** Offers in database but empty response
- **Cause:** Booking has `SelectedDriverId` set or status is Cancelled/Completed
- **Fix:** Reset `SelectedDriverId` to NULL or use active bookings

### Issue 2: Offers expire too quickly
- **Symptom:** Offers disappear after 2-5 minutes
- **Cause:** Default expiration time is 2-5 minutes
- **Fix:** Adjust expiration time in `BookingBroadcastConsumer` (line 71-72)

### Issue 3: No offers created
- **Symptom:** No `DriverBookingOffer` records in database
- **Cause:** Booking broadcast not triggered or no available drivers
- **Fix:** 
  - Trigger broadcast: `POST /api/bookings/{id}/start-broadcasting`
  - Ensure drivers are active and have matching vehicle types

## Related Endpoints

- `POST /api/bookings/{id}/start-broadcasting` - Manually trigger broadcast
- `GET /api/bookings/{id}` - Check booking status
- `POST /api/driver-offers/{id}/accept` - Accept an offer
- `POST /api/driver-offers/{id}/reject` - Reject an offer

