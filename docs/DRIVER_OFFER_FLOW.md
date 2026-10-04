# Driver Offer Flow Documentation

This document describes the complete flow of how drivers receive booking offers and accept them.

## Overview

The system uses a **sequential queue-based offer system** where drivers receive offers one at a time, ordered by distance (closest driver first). This ensures fair distribution and prevents multiple drivers from competing for the same booking simultaneously.

---

## Flow Diagram

```
1. Booking Created
   ↓
2. BookingBroadcastRequested Event Published
   ↓
3. BookingBroadcastConsumer Processes Event
   ↓
4. Driver Availability Service Finds Available Drivers
   ↓
5. Offers Created for ALL Available Drivers (with Sequence Numbers)
   ↓
6. Booking Status: BroadcastingToDrivers
   ↓
7. Hangfire Job: ProcessNextDriverInQueueAsync (runs every 10s)
   ↓
8. Only ONE Driver Gets Active Offer (based on SequenceNumber)
   ↓
9. Driver Calls GET /api/driver-offers/pending
   ↓
10. Driver Reviews Offer (with booking details & multi-stop info)
   ↓
11a. Driver Accepts → POST /api/driver-offers/{id}/accept
    OR
11b. Driver Rejects → POST /api/driver-offers/{id}/reject
    OR
11c. Offer Expires → Hangfire Job Processes Next Driver
```

---

## Detailed Flow Steps

### Step 1: Booking Creation

**Trigger:** Customer creates a booking via API

**Endpoints:**
- `POST /api/bookings` (Legacy)
- `POST /api/bookings/lalamove` (Multi-stop with Lalamove-style)

**What Happens:**
- Booking is created with status `Pending`
- Booking is saved to database
- For Lalamove-style bookings, multi-stop information is included

**Code Location:**
- `BookingHandlers.cs` → `CreateBookingCommandHandler`
- `LalamoveBookingHandlers.cs` → `CreateBookingLalamoveCommandHandler`

---

### Step 2: Broadcast Event Published

**Trigger:** Admin/Owner manually starts broadcasting OR system auto-triggers

**What Happens:**
- `BookingBroadcastRequested` event is published via MassTransit
- Event contains `BookingId`

**Code Location:**
- `BookingHandlers.cs` → `StartBroadcastingBookingCommandHandler`
- Publishes: `BookingBroadcastRequested` event

---

### Step 3: BookingBroadcastConsumer Processes Event

**Trigger:** MassTransit receives `BookingBroadcastRequested` event

**What Happens:**
1. Retrieves booking from database
2. Checks if already broadcasting (prevents duplicate processing)
3. Gets available drivers via `DriverAvailabilityService`
4. Calculates expiration time (2-5 minutes based on driver count)
5. Creates `DriverBookingOffer` entities for ALL available drivers
6. Sets booking status to `BroadcastingToDrivers`

**Key Details:**
- **All drivers get offers created** (not just one)
- Offers are created with `SequenceNumber` (sorted by distance)
- Offers have expiration time (2-5 minutes)
- Booking status changes to `BroadcastingToDrivers`

**Code Location:**
- `BookingBroadcastConsumer.cs` → `Consume` method

**Driver Filtering:**
- Active drivers only
- Drivers with matching vehicle type
- Drivers sorted by distance (closest first)
- Sequence numbers assigned based on distance order

---

### Step 4: Hangfire Background Jobs

**Two recurring jobs run every 10 seconds:**

#### Job 1: `ProcessExpiredOffersAsync`
- Finds all expired offers
- Marks them as `Expired`
- Checks if booking has any remaining pending offers
- If no pending offers remain → Booking marked as `RejectedByAllDrivers`

#### Job 2: `ProcessNextDriverInQueueAsync`
- Gets all bookings with status `BroadcastingToDrivers`
- For each booking, gets the **next pending offer** (lowest SequenceNumber)
- If current offer is expired → expires it and moves to next
- If no more pending offers → marks booking as `RejectedByAllDrivers`

**Key Point:** Only ONE driver at a time has an active (non-expired) offer per booking.

**Code Location:**
- `BookingBroadcastQueueService.cs`

---

### Step 5: Driver Retrieves Pending Offers

**Endpoint:** `GET /api/driver-offers/pending?limit=3`

**Authentication:** Requires JWT token with Driver role

**What Happens:**
1. Extracts `DriverId` from JWT token
2. Queries `DriverBookingOfferRepository.GetPendingOffersForDriverAsync()`
   - Returns up to 3 pending offers (configurable, max 10)
   - Filters out expired offers
   - Orders by `OfferedAt` (oldest first)
3. For each offer, fetches booking details with stops
4. Filters out:
   - Already assigned bookings (`SelectedDriverId` has value)
   - Cancelled/Completed/Rejected bookings
5. Returns `DriverBookingOfferWithDetailsDto` with:
   - Full offer details (distance, rating, expiration, etc.)
   - Complete booking information
   - **Multi-stop route details** (all pickup and dropoff locations)

**Response Example:**
```json
[
  {
    "id": "offer-guid",
    "bookingId": "booking-guid",
    "driverId": "driver-guid",
    "status": "Pending",
    "offeredAt": "2024-01-15T10:00:00Z",
    "expiresAt": "2024-01-15T10:05:00Z",
    "distanceKm": 5.2,
    "driverRating": 4.8,
    "estimatedArrivalMinutes": 15,
    "bookingNumber": "BKG-20240115-123456",
    "customerName": "John Doe",
    "vehicleType": "Closed Van",
    "cargoDescription": "Electronics",
    "scheduleDate": "2024-01-15T14:00:00Z",
    "estimatedFare": 150.00,
    "stops": [
      {
        "sequence": 0,
        "address": "123 Main St, City",
        "type": "Pickup",
        "latitude": 14.5995,
        "longitude": 120.9842,
        "contactName": "John Doe",
        "contactPhone": "+1234567890"
      },
      {
        "sequence": 1,
        "address": "456 Oak Ave, City",
        "type": "Dropoff",
        "latitude": 14.6042,
        "longitude": 120.9889,
        "contactName": "Jane Smith",
        "contactPhone": "+0987654321"
      }
    ]
  }
]
```

**Code Location:**
- `DriverOffersController.cs` → `GetPending` method
- `DriverOfferHandlers.cs` → `GetPendingOffersQueryHandler`

---

### Step 6: Driver Accepts Offer

**Endpoint:** `POST /api/driver-offers/{offerId}/accept`

**Authentication:** Requires JWT token with Driver role

**What Happens:**
1. Validates offer exists
2. Validates offer belongs to driver (from JWT token)
3. Validates offer is `Pending` status
4. Validates offer is not expired
5. **Race condition check:** Verifies no other driver already accepted
6. Calls `offer.Accept()` → Sets status to `Accepted`
7. Calls `booking.AcceptByDriver()` → Sets `SelectedDriverId`, updates status
8. **Cancels all other pending offers** for this booking
9. Saves changes

**Booking Status Changes:**
- `SelectedDriverId` = Driver's ID
- `Status` = `Confirmed` (or `DriverAssigned` depending on implementation)
- `DriverAssignedAt` = Current timestamp
- Legacy: `AssignmentStatus` = `AcceptedByDriver`

**Code Location:**
- `DriverOffersController.cs` → `Accept` method
- `DriverOfferHandlers.cs` → `AcceptBookingOfferCommandHandler`
- `Booking.cs` → `AcceptByDriver` method
- `DriverBookingOffer.cs` → `Accept` method

**Response:**
```json
{
  "id": "offer-guid",
  "bookingId": "booking-guid",
  "status": "Accepted",
  "respondedAt": "2024-01-15T10:02:30Z"
}
```

---

### Step 7: Driver Rejects Offer

**Endpoint:** `POST /api/driver-offers/{offerId}/reject`

**What Happens:**
1. Validates offer exists and belongs to driver
2. Calls `offer.Reject()` → Sets status to `Rejected`
3. Checks if all offers for booking are rejected/expired
4. If no pending offers remain → Booking marked as `RejectedByAllDrivers`
5. Hangfire job will move to next driver in queue

**Code Location:**
- `DriverOffersController.cs` → `Reject` method
- `DriverOfferHandlers.cs` → `RejectBookingOfferCommandHandler`

---

## Key Concepts

### Sequential Queue System

- **Not Round-Robin:** Offers are NOT distributed equally among drivers
- **Distance-Based Priority:** Closest driver gets first chance
- **One at a Time:** Only ONE driver has an active offer per booking at any time
- **Automatic Progression:** If driver rejects/expires, next driver automatically gets the offer

### Offer Expiration

- **Dynamic Expiration:** 2-5 minutes based on number of available drivers
  - More than 5 drivers → 2 minutes
  - 5 or fewer drivers → 5 minutes
- **Automatic Processing:** Hangfire jobs handle expiration every 10 seconds
- **Next Driver:** When offer expires, next driver in queue automatically gets it

### Multi-Stop Support

- Offers include **full multi-stop route information**
- Each stop includes:
  - Sequence number
  - Address
  - Type (Pickup/Dropoff)
  - Coordinates (Latitude/Longitude)
  - Contact information
  - Notes
- Drivers can see complete route before accepting

### Race Condition Prevention

- When driver accepts, system checks if another driver already accepted
- All other pending offers are immediately cancelled
- Booking is locked to the accepting driver

---

## API Endpoints Summary

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/driver-offers/pending?limit=3` | Get pending offers (with booking details & stops) |
| `POST` | `/api/driver-offers/{id}/accept` | Accept an offer |
| `POST` | `/api/driver-offers/{id}/reject` | Reject an offer |

---

## Status Flow

### Booking Statuses:
1. `Pending` → Booking created, waiting for driver
2. `BroadcastingToDrivers` → Offers sent to drivers
3. `Confirmed` → Driver accepted offer
4. `DriverAssigned` → Driver en route to pickup
5. `PickedUp` → Driver picked up items
6. `InTransit` → Driver delivering
7. `Completed` → All stops completed
8. `Cancelled` → Cancelled by customer/driver
9. `RejectedByAllDrivers` → All drivers rejected/expired

### Offer Statuses:
1. `Pending` → Waiting for driver response
2. `Accepted` → Driver accepted
3. `Rejected` → Driver rejected
4. `Expired` → Offer expired (timeout)

---

## Background Jobs

### Hangfire Recurring Jobs:

1. **ProcessExpiredOffersAsync**
   - Frequency: Every 10 seconds
   - Purpose: Mark expired offers and check if booking should be rejected

2. **ProcessNextDriverInQueueAsync**
   - Frequency: Every 10 seconds
   - Purpose: Move to next driver in queue when current offer expires/rejects

---

## Error Scenarios

### Offer Already Accepted
- **Error:** "Another driver has already accepted this booking"
- **Cause:** Race condition - another driver accepted before this one
- **Solution:** Offer is automatically cancelled, driver should refresh offers

### Offer Expired
- **Error:** "Offer has expired"
- **Cause:** Driver took too long to respond
- **Solution:** Driver should refresh offers to see next available booking

### Booking Already Assigned
- **Filter:** Offers API automatically filters out assigned bookings
- **Cause:** Booking was assigned to another driver
- **Solution:** Driver won't see this booking in pending offers

---

## Best Practices

1. **Polling Frequency:** Drivers should poll `/pending` every 5-10 seconds for real-time updates
2. **Quick Response:** Drivers should accept/reject within expiration window (2-5 minutes)
3. **Error Handling:** Handle expired offers gracefully, refresh list
4. **Multi-Stop Review:** Review all stops before accepting to ensure route is feasible

---

## Technical Notes

- **Event-Driven:** Uses MassTransit for async event processing
- **Background Processing:** Hangfire for scheduled jobs
- **Database:** Entity Framework Core with repository pattern
- **Authentication:** JWT Bearer tokens
- **Validation:** Domain-level validation in entity methods
- **Race Conditions:** Handled via database checks and offer cancellation

