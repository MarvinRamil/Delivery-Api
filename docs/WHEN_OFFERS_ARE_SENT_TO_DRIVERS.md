# When Are Offers Sent to Drivers?

This document explains **when** and **what triggers** a booking to be offered to drivers.

## Overview

The system uses a **manual trigger** model where an admin/owner must explicitly start broadcasting a booking. Offers are not automatically created when a booking is created.

---

## Trigger Flow

```
1. Booking Created
   ↓
2. Admin/Owner Manually Triggers Broadcast
   ↓
3. StartBroadcastingBookingCommand Executed
   ↓
4. BookingBroadcastRequested Event Published (via MassTransit)
   ↓
5. BookingBroadcastConsumer Processes Event
   ↓
6. Driver Availability Service Finds Available Drivers
   ↓
7. Offers Created for ALL Available Drivers
   ↓
8. Hangfire Job Processes Offers Sequentially (one at a time)
```

---

## Step-by-Step Breakdown

### Step 1: Booking Created

**When:** Customer creates a booking via API

**Endpoints:**
- `POST /api/bookings` (Legacy)
- `POST /api/bookings/lalamove` (Multi-stop)

**What Happens:**
- Booking is created with `Status = Pending`
- Booking is saved to database
- **NO offers are created at this point**

**Code Location:**
- `BookingHandlers.cs` → `CreateBookingCommandHandler`
- `LalamoveBookingHandlers.cs` → `CreateBookingLalamoveCommandHandler`

---

### Step 2: Manual Broadcast Trigger

**When:** Admin/Owner manually starts broadcasting

**Endpoint:**
```
POST /api/bookings/{id}/start-broadcast
```

**Authorization:** Requires `SuperAdmin` or `Admin` role (Backoffice policy)

**What Happens:**
1. Validates booking exists
2. Checks booking is classified as `Small` (only small bookings can be broadcast)
3. Executes `StartBroadcastingBookingCommand`
4. Publishes `BookingBroadcastRequested` event via MassTransit

**Code Location:**
- `BookingsController.cs` → `StartBroadcast` method (line 431-439)
- `BookingHandlers.cs` → `StartBroadcastingBookingCommandHandler` (line 477-506)

**Important:** This is a **manual step**. Bookings are NOT automatically broadcast when created.

---

### Step 3: BookingBroadcastRequested Event Published

**When:** `StartBroadcastingBookingCommand` executes

**What Happens:**
- Event is published to MassTransit message queue
- Contains `BookingId`
- Event is processed asynchronously

**Code Location:**
- `BookingHandlers.cs` → `StartBroadcastingBookingCommandHandler.Handle()` (line 502)

---

### Step 4: BookingBroadcastConsumer Processes Event

**When:** MassTransit receives `BookingBroadcastRequested` event

**What Happens:**
1. Retrieves booking from database
2. Checks if already broadcasting (prevents duplicate processing)
3. Gets available drivers via `DriverAvailabilityService`
4. Calculates expiration time (2-5 minutes based on driver count)
5. **Creates `DriverBookingOffer` entities for ALL available drivers**
6. Sets booking `AssignmentStatus = BroadcastingToDrivers`

**Code Location:**
- `BookingBroadcastConsumer.cs` → `Consume` method (line 33-95)

---

### Step 5: Driver Availability Service Finds Drivers

**When:** `BookingBroadcastConsumer` calls `GetAvailableDriversAsync()`

**What Filters Are Applied:**

1. **Active Drivers Only**
   - Driver must be online/active
   - Driver must not be on another dispatch

2. **Vehicle Type Match**
   - Driver's vehicle type must match booking's vehicle type
   - Small vehicle types: "Closed Van", "L300" (configurable)

3. **Distance Calculation**
   - Calculates distance from driver's current location to booking pickup
   - Uses geospatial queries (Redis or database)

4. **Sorting**
   - Drivers sorted by distance (closest first)
   - Sequence numbers assigned based on distance order

**Code Location:**
- `IDriverAvailabilityService.cs` → `GetAvailableDriversAsync()`

---

### Step 6: Offers Created for All Drivers

**When:** After available drivers are found

**What Happens:**
- **ALL available drivers get offers created** (not just one)
- Each offer has:
  - `DriverId` - The driver who will receive it
  - `BookingId` - The booking being offered
  - `SequenceNumber` - Order in queue (1 = closest driver)
  - `ExpiresAt` - Expiration time (2-5 minutes)
  - `DistanceKm` - Distance from driver to pickup
  - `Status = Pending`

**Expiration Time:**
- If > 5 drivers available: 2 minutes
- If ≤ 5 drivers available: 5 minutes

**Code Location:**
- `BookingBroadcastConsumer.cs` → Lines 74-87

---

### Step 7: Hangfire Background Jobs Process Offers

**When:** Two recurring jobs run every 10 seconds

#### Job 1: `ProcessExpiredOffersAsync`
- Finds all expired offers
- Marks them as `Expired`
- Checks if booking has any remaining pending offers
- If no pending offers remain → Booking marked as `RejectedByAllDrivers`

#### Job 2: `ProcessNextDriverInQueueAsync`
- Gets all bookings with `AssignmentStatus = BroadcastingToDrivers`
- For each booking, gets the **next pending offer** (lowest `SequenceNumber`)
- If current offer is expired → expires it and moves to next
- If no more pending offers → marks booking as `RejectedByAllDrivers`

**Key Point:** Only ONE driver at a time has an active (non-expired) offer per booking.

**Code Location:**
- `BookingBroadcastQueueService.cs` → `ProcessNextDriverInQueueAsync()` (line 95-177)

---

## When Drivers See Offers

### Driver Calls API
```
GET /api/driver-offers/pending?limit=3
```

### What They See
- Only offers where:
  - `DriverId` matches their user ID
  - `Status = Pending`
  - `ExpiresAt > now` (not expired)
  - Booking `SelectedDriverId = NULL` (not assigned)
  - Booking `Status` is NOT `Cancelled` or `Completed`

### Sequential Processing
- Even though ALL drivers get offers created, only ONE driver sees an active offer at a time
- The Hangfire job ensures only the driver with the lowest `SequenceNumber` has a non-expired offer
- When that offer expires or is rejected, the next driver in queue gets an active offer

---

## Summary: What Triggers Offers?

### ✅ Manual Trigger (Required)
- Admin/Owner must call: `POST /api/bookings/{id}/start-broadcast`
- This is **NOT automatic** - bookings are NOT automatically offered when created

### ✅ Prerequisites
1. Booking must exist
2. Booking must be classified as `Small` (only small bookings can be broadcast)
3. Booking must not already be broadcasting

### ✅ What Happens When Triggered
1. Event published to MassTransit
2. Consumer finds available drivers
3. Offers created for ALL available drivers
4. Hangfire jobs process offers sequentially (one at a time)

### ❌ What Does NOT Trigger Offers
- Booking creation (manual trigger required)
- Automatic scheduling (manual trigger required)
- Customer actions (only admin/owner can trigger)

---

## API Endpoints

### Start Broadcasting (Admin/Owner Only)
```
POST /api/bookings/{id}/start-broadcast
Authorization: Bearer <admin-token>
```

**Response:**
```json
{
  "success": true,
  "message": "Broadcast started"
}
```

### Get Pending Offers (Driver)
```
GET /api/driver-offers/pending?limit=3
Authorization: Bearer <driver-token>
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": "offer-guid",
      "bookingId": "booking-guid",
      "status": "Pending",
      "expiresAt": "2024-01-15T10:05:00Z",
      "distanceKm": 5.2,
      "sequenceNumber": 1,
      // ... full booking details
    }
  ]
}
```

---

## Configuration

### Small Vehicle Types
Configured in `appsettings.json`:
```json
{
  "BookingSettings": {
    "SmallVehicleTruckTypes": ["Closed Van", "L300"]
  }
}
```

### Hangfire Jobs
- `ProcessExpiredOffersAsync` - Runs every 10 seconds
- `ProcessNextDriverInQueueAsync` - Runs every 10 seconds

Configured in `Program.cs` or Hangfire dashboard.

---

## Troubleshooting

### No Offers Created
1. ✅ Check if broadcast was triggered: `POST /api/bookings/{id}/start-broadcast`
2. ✅ Check booking `AssignmentStatus` = `BroadcastingToDrivers`
3. ✅ Check if drivers exist and are active
4. ✅ Check if drivers have matching vehicle types
5. ✅ Check driver locations (geospatial data)

### Offers Created But Driver Doesn't See Them
1. ✅ Check `DriverId` matches driver's user ID
2. ✅ Check offer `Status = Pending`
3. ✅ Check `ExpiresAt > now`
4. ✅ Check booking `SelectedDriverId = NULL`
5. ✅ Check booking `Status` is NOT `Cancelled` or `Completed`
6. ✅ Check Hangfire job is running (offers might be expired)

### Offers Expire Too Quickly
- Default expiration: 2-5 minutes
- If > 5 drivers: 2 minutes
- If ≤ 5 drivers: 5 minutes
- Adjust in `BookingBroadcastConsumer.cs` (line 71-72)

