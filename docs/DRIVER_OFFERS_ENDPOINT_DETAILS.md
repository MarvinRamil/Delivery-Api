# Driver Offers Endpoint - Complete Details

This document provides comprehensive details about the driver offers endpoint, including module structure, architecture, data flow, and implementation details.

---

## Module Information

### Module Name
**`BeeLogistics.Modules.Sales`**

### Module Purpose
The Sales module handles:
- Booking management
- Customer management
- Driver booking offers
- Pricing and fare calculation
- Multi-stop delivery support

### Module Structure
```
BeeLogistics.Modules.Sales/
├── Application/
│   ├── Consumers/          # MassTransit event consumers
│   │   └── BookingBroadcastConsumer.cs
│   ├── DTOs/               # Data Transfer Objects
│   │   └── BookingDto.cs   # Contains DriverBookingOfferWithDetailsDto
│   ├── Handlers/           # MediatR command/query handlers
│   │   └── DriverOfferHandlers.cs
│   ├── Interfaces/         # Repository interfaces
│   │   └── IDriverBookingOfferRepository.cs
│   └── Services/           # Business logic services
│       └── IDriverAvailabilityService.cs
├── Domain/                 # Domain entities
│   └── DriverBookingOffer.cs
├── Infrastructure/
│   ├── Repositories/       # Data access implementations
│   │   └── DriverBookingOfferRepository.cs
│   └── Services/           # Infrastructure services
│       └── BookingBroadcastQueueService.cs
└── Presentation/
    └── Controllers/        # API controllers
        └── DriverOffersController.cs
```

---

## API Endpoints

### Base Route
```
/api/driver-offers
```

### Endpoints

#### 1. Get Pending Offers
```
GET /api/driver-offers/pending?limit=3
```

**Authorization:** Requires JWT Bearer token (any authenticated user)

**Query Parameters:**
- `limit` (optional, default: 3, max: 10) - Number of offers to return

**Request Headers:**
```
Authorization: Bearer <jwt-token>
```

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": "offer-guid",
      "bookingId": "booking-guid",
      "driverId": "driver-guid",
      "tenantId": "tenant-guid",
      "status": "Pending",
      "offeredAt": "2024-01-15T10:00:00Z",
      "respondedAt": null,
      "expiresAt": "2024-01-15T10:05:00Z",
      "sequenceNumber": 1,
      "distanceKm": 5.2,
      "isFavouriteDriver": false,
      "driverRating": 4.8,
      "estimatedArrivalMinutes": 15,
      "bookingNumber": "BKG-20240115-123456",
      "customerId": "customer-guid",
      "customerName": "John Doe",
      "vehicleType": "Closed Van",
      "cargoDescription": "Electronics",
      "scheduleDate": "2024-01-15T14:00:00Z",
      "bookingStatus": "Pending",
      "notes": "Handle with care",
      "weightKg": 50.0,
      "itemImagePath": "/uploads/bookings/.../item-image.webp",
      "estimatedFare": 150.00,
      "finalFare": null,
      "distanceKmTotal": 5.2,
      "stops": [
        {
          "sequence": 0,
          "address": "123 Main St, City",
          "type": "Pickup",
          "latitude": 14.5995,
          "longitude": 120.9842,
          "contactName": "John Doe",
          "contactPhone": "+1234567890",
          "notes": null
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
}
```

**Status Codes:**
- `200 OK` - Success
- `401 Unauthorized` - Missing or invalid token
- `400 Bad Request` - Invalid limit parameter

---

#### 2. Accept Offer
```
POST /api/driver-offers/{id}/accept
```

**Authorization:** Requires JWT Bearer token (driver must own the offer)

**Path Parameters:**
- `id` (Guid) - Offer ID

**Request Headers:**
```
Authorization: Bearer <jwt-token>
```

**Response:**
```json
{
  "success": true,
  "data": {
    "id": "offer-guid",
    "bookingId": "booking-guid",
    "driverId": "driver-guid",
    "tenantId": "tenant-guid",
    "status": "Accepted",
    "offeredAt": "2024-01-15T10:00:00Z",
    "respondedAt": "2024-01-15T10:02:00Z",
    "expiresAt": "2024-01-15T10:05:00Z",
    "sequenceNumber": 1,
    "distanceKm": 5.2
  }
}
```

**Status Codes:**
- `200 OK` - Offer accepted successfully
- `400 Bad Request` - Offer not pending, expired, or already accepted
- `401 Unauthorized` - Missing or invalid token
- `404 Not Found` - Offer not found

**What Happens:**
1. Validates offer exists and belongs to driver
2. Checks offer is pending and not expired
3. Checks booking not already accepted by another driver
4. Marks offer as `Accepted`
5. Updates booking: `SelectedDriverId` = driver ID, `Status` = `Confirmed`
6. Cancels all other pending offers for this booking

---

#### 3. Reject Offer
```
POST /api/driver-offers/{id}/reject
```

**Authorization:** Requires JWT Bearer token (driver must own the offer)

**Path Parameters:**
- `id` (Guid) - Offer ID

**Request Headers:**
```
Authorization: Bearer <jwt-token>
```

**Response:**
```json
{
  "success": true,
  "message": "Offer rejected"
}
```

**Status Codes:**
- `200 OK` - Offer rejected successfully
- `400 Bad Request` - Offer not pending
- `401 Unauthorized` - Missing or invalid token
- `404 Not Found` - Offer not found

**What Happens:**
1. Validates offer exists and belongs to driver
2. Checks offer is pending
3. Marks offer as `Rejected`
4. Checks if all offers for booking are rejected/expired
5. If no pending offers remain → Booking marked as `RejectedByAllDrivers`
6. Hangfire job will process next driver in queue

---

## When Offers Are Created

### Trigger Flow

```
1. Booking Created
   ↓
2. Admin/Owner Triggers Broadcast
   POST /api/bookings/{id}/start-broadcast
   ↓
3. StartBroadcastingBookingCommand Executed
   ↓
4. BookingBroadcastRequested Event Published (MassTransit)
   ↓
5. BookingBroadcastConsumer Processes Event
   ↓
6. DriverAvailabilityService Finds Available Drivers
   ↓
7. Offers Created for ALL Available Drivers
   ↓
8. Hangfire Jobs Process Offers Sequentially
```

### Detailed Creation Process

#### Step 1: Manual Broadcast Trigger
**Endpoint:** `POST /api/bookings/{id}/start-broadcast`

**Prerequisites:**
- Booking must exist
- Booking must be classified as `Small` (only small bookings can be broadcast)
- User must have `SuperAdmin` or `Admin` role

**Code Location:**
- `BookingsController.cs` → `StartBroadcast` method (line 431-439)
- `BookingHandlers.cs` → `StartBroadcastingBookingCommandHandler` (line 477-506)

#### Step 2: Event Publishing
**Event:** `BookingBroadcastRequested`

**Published By:**
- `StartBroadcastingBookingCommandHandler.Handle()` (line 502)

**Event Properties:**
- `BookingId` (Guid)

**Transport:** MassTransit (RabbitMQ)

#### Step 3: Consumer Processing
**Consumer:** `BookingBroadcastConsumer`

**Code Location:** `Application/Consumers/BookingBroadcastConsumer.cs`

**Process:**
1. Retrieves booking from database
2. Checks if already broadcasting (prevents duplicate processing)
3. Gets available drivers via `DriverAvailabilityService`
4. Calculates expiration time (2-5 minutes based on driver count)
5. Creates `DriverBookingOffer` entities for ALL available drivers
6. Sets booking `AssignmentStatus = BroadcastingToDrivers`

**Driver Filtering:**
- Active drivers only (online, not on another dispatch)
- Vehicle type match (must match booking's vehicle type)
- Distance calculation (sorted by closest first)
- Sequence numbers assigned (1 = closest driver)

**Code Location:** `BookingBroadcastConsumer.cs` → `Consume` method (line 33-95)

#### Step 4: Offer Creation
**Entity:** `DriverBookingOffer`

**Created For:** ALL available drivers (not just one)

**Properties Set:**
- `BookingId` - The booking being offered
- `DriverId` - The driver who will receive it
- `Status = Pending`
- `OfferedAt = DateTime.UtcNow`
- `ExpiresAt` - 2-5 minutes from now
- `SequenceNumber` - Order in queue (1 = closest)
- `DistanceKm` - Distance from driver to pickup
- `IsFavouriteDriver` - If customer marked as favourite
- `DriverRating` - Driver's average rating
- `EstimatedArrivalMinutes` - Estimated time to pickup

**Code Location:** `BookingBroadcastConsumer.cs` → Lines 74-87

---

## Architecture & Data Flow

### Request Flow (Get Pending Offers)

```
1. HTTP Request
   GET /api/driver-offers/pending?limit=3
   ↓
2. DriverOffersController.GetPending()
   - Extracts DriverId from JWT token
   - Validates token
   ↓
3. MediatR Query
   GetPendingOffersQuery(driverId, limit)
   ↓
4. GetPendingOffersQueryHandler.Handle()
   - Calls DriverBookingOfferRepository.GetPendingOffersForDriverAsync()
   - Filters: DriverId, Status=Pending, ExpiresAt > now
   - Orders by OfferedAt (oldest first)
   - Limits to 3 offers (configurable, max 10)
   ↓
5. Repository Query (Database)
   SELECT * FROM DriverBookingOffers
   WHERE DriverId = @driverId
     AND Status = 'Pending'
     AND ExpiresAt > NOW()
   ORDER BY OfferedAt
   LIMIT 3
   ↓
6. Handler Enriches Data
   - For each offer, fetches booking details
   - Includes stops (multi-stop information)
   - Filters out: assigned bookings, cancelled/completed
   - Maps to DriverBookingOfferWithDetailsDto
   ↓
7. Response
   Returns list of offers with full booking details
```

### Database Schema

#### Table: `DriverBookingOffers` (Schema: `sales`)

```sql
CREATE TABLE sales."DriverBookingOffers" (
    "Id" UUID PRIMARY KEY,
    "BookingId" UUID NOT NULL,
    "DriverId" UUID NOT NULL,
    "Status" VARCHAR(20) NOT NULL,  -- Pending, Accepted, Rejected, Expired
    "OfferedAt" TIMESTAMP NOT NULL,
    "RespondedAt" TIMESTAMP NULL,
    "ExpiresAt" TIMESTAMP NOT NULL,
    "DistanceKm" DECIMAL(10,2) NULL,
    "IsFavouriteDriver" BOOLEAN NOT NULL DEFAULT FALSE,
    "DriverRating" DECIMAL(3,2) NULL,
    "EstimatedArrivalMinutes" INTEGER NULL,
    "SequenceNumber" INTEGER NOT NULL,  -- Legacy field
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP NULL,
    "DeletedAt" TIMESTAMP NULL,
    
    FOREIGN KEY ("BookingId") REFERENCES sales."Bookings"("Id") ON DELETE CASCADE
);

-- Indexes
CREATE INDEX "IX_DriverBookingOffers_BookingId" ON sales."DriverBookingOffers"("BookingId");
CREATE INDEX "IX_DriverBookingOffers_DriverId" ON sales."DriverBookingOffers"("DriverId");
CREATE INDEX "IX_DriverBookingOffers_DriverId_Status" ON sales."DriverBookingOffers"("DriverId", "Status");
CREATE INDEX "IX_DriverBookingOffers_Status" ON sales."DriverBookingOffers"("Status");
CREATE INDEX "IX_DriverBookingOffers_ExpiresAt" ON sales."DriverBookingOffers"("ExpiresAt");
CREATE INDEX "IX_DriverBookingOffers_IsFavouriteDriver" ON sales."DriverBookingOffers"("IsFavouriteDriver");
```

**Code Location:** `Infrastructure/SalesDbContext.cs` → Lines 187-210

---

## Domain Model

### Entity: `DriverBookingOffer`

**Namespace:** `BeeLogistics.Modules.Sales.Domain`

**Inherits From:** `Entity` (base class with `Id`, `CreatedAt`, `UpdatedAt`, `DeletedAt`)

**Properties:**
```csharp
public Guid BookingId { get; private set; }
public Guid DriverId { get; private set; }
public DriverOfferStatus Status { get; private set; }
public DateTime OfferedAt { get; private set; }
public DateTime? RespondedAt { get; private set; }
public DateTime ExpiresAt { get; private set; }
public decimal? DistanceKm { get; private set; }
public bool IsFavouriteDriver { get; private set; }
public decimal? DriverRating { get; private set; }
public int? EstimatedArrivalMinutes { get; private set; }
public int SequenceNumber { get; private set; }  // Legacy
```

**Methods:**
- `Accept()` - Marks offer as accepted
- `Reject()` - Marks offer as rejected
- `Expire()` - Marks offer as expired
- `IsExpired` - Property to check if offer is expired

**Code Location:** `Domain/DriverBookingOffer.cs`

### Enum: `DriverOfferStatus`

```csharp
public enum DriverOfferStatus
{
    Pending,    // Offer is active and waiting for driver response
    Accepted,   // Driver accepted the offer
    Rejected,   // Driver rejected the offer
    Expired     // Offer expired without response
}
```

---

## Repository Pattern

### Interface: `IDriverBookingOfferRepository`

**Namespace:** `BeeLogistics.Modules.Sales.Application.Interfaces`

**Methods:**
```csharp
Task<IReadOnlyList<DriverBookingOffer>> GetByBookingIdAsync(Guid bookingId, CancellationToken ct);
Task<DriverBookingOffer?> GetPendingOfferForDriverAsync(Guid driverId, CancellationToken ct);
Task<IReadOnlyList<DriverBookingOffer>> GetPendingOffersForDriverAsync(Guid driverId, int limit, CancellationToken ct);
Task<DriverBookingOffer?> GetNextPendingOfferForBookingAsync(Guid bookingId, CancellationToken ct);
Task<IReadOnlyList<DriverBookingOffer>> GetExpiredOffersAsync(CancellationToken ct);
Task CancelAllPendingOffersForBookingAsync(Guid bookingId, CancellationToken ct);
```

**Code Location:** `Application/Interfaces/IDriverBookingOfferRepository.cs`

### Implementation: `DriverBookingOfferRepository`

**Namespace:** `BeeLogistics.Modules.Sales.Infrastructure.Repositories`

**Inherits From:** `Repository<DriverBookingOffer, SalesDbContext>`

**Database Context:** `SalesDbContext` (PostgreSQL)

**Code Location:** `Infrastructure/Repositories/DriverBookingOfferRepository.cs`

---

## Background Jobs (Hangfire)

### Job 1: Process Expired Offers
**Method:** `ProcessExpiredOffersAsync()`

**Schedule:** Runs every 10 seconds

**What It Does:**
1. Finds all expired offers (`Status = Pending` AND `ExpiresAt < now`)
2. Marks them as `Expired`
3. Checks if booking has any remaining pending offers
4. If no pending offers remain → Booking marked as `RejectedByAllDrivers`

**Code Location:** `Infrastructure/Services/BookingBroadcastQueueService.cs` → Line 27-88

### Job 2: Process Next Driver in Queue
**Method:** `ProcessNextDriverInQueueAsync()`

**Schedule:** Runs every 10 seconds

**What It Does:**
1. Gets all bookings with `AssignmentStatus = BroadcastingToDrivers`
2. For each booking, gets the **next pending offer** (lowest `SequenceNumber`)
3. If current offer is expired → expires it and moves to next
4. If no more pending offers → marks booking as `RejectedByAllDrivers`

**Key Point:** Only ONE driver at a time has an active (non-expired) offer per booking.

**Code Location:** `Infrastructure/Services/BookingBroadcastQueueService.cs` → Line 95-177

---

## Filters & Conditions

### Repository Level Filters (Database Query)

When fetching pending offers:
- `DriverId` must match the logged-in driver
- `Status = Pending`
- `ExpiresAt > now` (not expired)
- Ordered by `OfferedAt` (oldest first)
- Limited to `limit` offers (default 3, max 10)

**Code Location:** `DriverBookingOfferRepository.GetPendingOffersForDriverAsync()` (line 28-38)

### Handler Level Filters (After Fetching)

After fetching offers from database, additional filters:
- Booking must exist
- `Booking.SelectedDriverId` must be NULL (not already assigned)
- `Booking.Status` must NOT be `Cancelled` or `Completed`

**Code Location:** `DriverOfferHandlers.cs` → `GetPendingOffersQueryHandler.Handle()` (line 30-146)

---

## DTOs (Data Transfer Objects)

### `DriverBookingOfferDto`
Basic offer information (used for accept/reject responses)

**Properties:**
- `Id`, `BookingId`, `DriverId`, `TenantId`
- `Status`, `OfferedAt`, `RespondedAt`, `ExpiresAt`
- `SequenceNumber`, `DistanceKm`

**Code Location:** `Application/DTOs/BookingDto.cs` → Line 63-77

### `DriverBookingOfferWithDetailsDto`
Enhanced offer with full booking details (used for pending offers)

**Additional Properties:**
- Full booking information (booking number, customer, vehicle type, etc.)
- Multi-stop information (`List<DeliveryStopDto>`)
- Pricing information (estimated fare, final fare)
- Customer details

**Code Location:** `Application/DTOs/BookingDto.cs` → Line 79-110

### `DeliveryStopDto`
Multi-stop information

**Properties:**
- `Sequence`, `Address`, `Type` (Pickup/Dropoff)
- `Latitude`, `Longitude`
- `ContactName`, `ContactPhone`, `Notes`

**Code Location:** `Application/DTOs/BookingDto.cs` → Line 112-122

---

## Dependencies

### Required Services
- `IDriverBookingOfferRepository` - Data access
- `IBookingRepository` - Booking data access
- `IDriverAvailabilityService` - Driver filtering and matching
- `IMediator` (MediatR) - Command/query handling
- `IPublishEndpoint` (MassTransit) - Event publishing

### External Dependencies
- **PostgreSQL** - Database (via Entity Framework Core)
- **RabbitMQ** - Message broker (via MassTransit)
- **Hangfire** - Background job processing
- **Redis** - Geospatial queries for driver location (optional)

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
Configured in `Program.cs`:
```csharp
RecurringJob.AddOrUpdate<BookingBroadcastQueueService>(
    "process-expired-offers",
    x => x.ProcessExpiredOffersAsync(),
    "*/10 * * * * *");  // Every 10 seconds

RecurringJob.AddOrUpdate<BookingBroadcastQueueService>(
    "process-next-driver",
    x => x.ProcessNextDriverInQueueAsync(),
    "*/10 * * * * *");  // Every 10 seconds
```

---

## Security & Authorization

### Authentication
- All endpoints require JWT Bearer token
- Token must contain `sub` or `NameIdentifier` claim with driver's user ID

### Authorization
- **Get Pending Offers:** Any authenticated user (extracts driver ID from token)
- **Accept/Reject:** Driver must own the offer (validated in handler)

### Validation
- Offer ownership validated (driver ID must match)
- Offer status validated (must be pending)
- Expiration checked (expired offers cannot be accepted)
- Race condition checks (prevents multiple drivers accepting same booking)

---

## Error Handling

### Common Errors

1. **401 Unauthorized**
   - Missing or invalid JWT token
   - Driver ID not found in token

2. **400 Bad Request**
   - Offer not pending
   - Offer expired
   - Offer already accepted/rejected
   - Invalid limit parameter

3. **404 Not Found**
   - Offer not found
   - Booking not found

4. **500 Internal Server Error**
   - Database connection issues
   - Unexpected exceptions

---

## Testing

### Unit Tests
- Handler logic
- Domain entity methods
- Repository queries

### Integration Tests
- End-to-end API calls
- Database operations
- Event publishing/consuming

### Manual Testing
1. Create a booking
2. Trigger broadcast: `POST /api/bookings/{id}/start-broadcast`
3. Check offers: `GET /api/driver-offers/pending`
4. Accept offer: `POST /api/driver-offers/{id}/accept`

---

## Related Documentation

- `docs/DRIVER_OFFER_FLOW.md` - Complete flow documentation
- `docs/WHEN_OFFERS_ARE_SENT_TO_DRIVERS.md` - Trigger details
- `docs/DRIVER_OFFERS_FILTERS.md` - Filter conditions

---

## Code Locations Summary

| Component | File Path |
|-----------|-----------|
| Controller | `Presentation/Controllers/DriverOffersController.cs` |
| Handlers | `Application/Handlers/DriverOfferHandlers.cs` |
| Domain Entity | `Domain/DriverBookingOffer.cs` |
| Repository Interface | `Application/Interfaces/IDriverBookingOfferRepository.cs` |
| Repository Implementation | `Infrastructure/Repositories/DriverBookingOfferRepository.cs` |
| DTOs | `Application/DTOs/BookingDto.cs` |
| Consumer | `Application/Consumers/BookingBroadcastConsumer.cs` |
| Background Jobs | `Infrastructure/Services/BookingBroadcastQueueService.cs` |
| Database Context | `Infrastructure/SalesDbContext.cs` |

