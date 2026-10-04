# Breaking Changes - Lalamove Clone Redesign

This document outlines all breaking changes introduced in the Lalamove-style redesign of the Bee Logistics platform.

**Version**: 2.0.0  
**Date**: January 2026  
**Migration Guide**: See [Migration Guide](#migration-guide) section below

---

## Table of Contents

1. [Role Simplification](#role-simplification)
2. [Booking System Redesign](#booking-system-redesign)
3. [API Endpoint Changes](#api-endpoint-changes)
4. [Database Schema Changes](#database-schema-changes)
5. [Configuration Changes](#configuration-changes)
6. [Frontend Breaking Changes](#frontend-breaking-changes)
7. [Migration Guide](#migration-guide)

---

## Role Simplification

### Removed Roles

The following roles have been **removed** and are no longer supported:

- `Owner` - Fleet business owner
- `Admin` - Administrative user (fleet management)
- `Dispatcher` - Dispatch manager
- `BusinessClient` - Business customer

### New Role Structure

**Main App Roles:**
- `Customer` - End users who book deliveries (previously `Client`)
- `Driver` - Independent drivers who accept deliveries

**Backoffice Role (separate system):**
- `SuperAdmin` - Platform administration (CRM, system management)

### Code Changes

**File**: `BeeLogistics.Shared/Abstractions/UserRoles.cs`

- Removed constants: `Owner`, `Admin`, `Dispatcher`, `BusinessClient`
- Renamed: `Client` → `Customer` (backward compatible alias exists)
- Removed role groups: `FleetManagement`, `FleetAll`, `Clients`
- Updated: `All` now only includes `SuperAdmin`, `Customer`, `Driver`

**File**: `BeeLogistics.Modules.Identity/Domain/ApplicationUser.cs`

- `CompanyId` - **Deprecated** (marked with `[Obsolete]`, will be removed in future version)
- `BusinessType` - **Deprecated** (no fleet vs individual distinction)
- `IsSoloDriver` - **Deprecated** (all drivers are now independent)
- `IsOnline` - **Kept** (driver availability status)

### Migration Steps

1. Update all role checks in code to use `Customer` instead of `Client`
2. Remove all references to `Owner`, `Admin`, `Dispatcher`, `BusinessClient` roles
3. Update authorization attributes and policies
4. Migrate existing users:
   - `Owner`/`Admin`/`Dispatcher` → Convert to `SuperAdmin` (backoffice) or remove
   - `BusinessClient` → Convert to `Customer`
   - `Client` → Rename to `Customer`

---

## Booking System Redesign

### Multi-Stop Support

Bookings now support **up to 20 stops** (1 pickup + up to 19 dropoffs) instead of single pickup/dropoff.

### New Booking Entity Structure

**New Fields:**
- `Stops` (collection) - List of `DeliveryStop` entities
- `SelectedDriverId` - Driver who accepted the booking
- `EstimatedFare` - Calculated fare before booking
- `FinalFare` - Actual fare (may differ if route changes)
- `DistanceKm` - Total distance
- `PriorityFee` - Optional priority surcharge
- `ServiceType` - `Immediate` or `Scheduled`
- `ScheduledDateTime` - For scheduled deliveries
- `FavouriteDriverId` - Customer's preferred driver

**Deprecated Fields (kept for backward compatibility):**
- `PickupLocation` - Use `Stops` collection instead
- `DropoffLocation` - Use `Stops` collection instead
- `BookingSize` - No longer used
- `BookingAssignmentStatus` - Use `BookingStatus` instead
- `AssignedToTenantId` - Use `SelectedDriverId` instead
- `AssignedByUserId` - No longer used
- `BeeTenantId` - No longer used

### New Booking Status Flow

**Old Statuses** (deprecated):
- `Dispatched` → Use `DriverAssigned`
- `OnTheWayToPickup` → Use `DriverAssigned`
- `InProgress` → Use `InTransit`
- `Delivered` → Use `Completed`

**New Statuses:**
- `Pending` - Customer created, waiting for driver acceptance
- `Confirmed` - Driver accepted
- `DriverAssigned` - Driver en route to pickup
- `PickedUp` - Driver picked up items
- `InTransit` - Driver delivering
- `Completed` - All stops completed
- `Cancelled` - Cancelled by customer or driver

### New Entities

**DeliveryStop**
- Represents a pickup or dropoff location
- Fields: `Sequence`, `Address`, `Latitude`, `Longitude`, `ContactName`, `ContactPhone`, `Notes`, `Type`, `Status`

**ProofOfDelivery**
- POD for each delivery stop
- Fields: `StopId`, `ImagePath`, `SignaturePath`, `DeliveredAt`, `RecipientName`, `Notes`

**FavouriteDriver**
- Customer's saved favourite drivers
- Fields: `CustomerId`, `DriverId`, `AddedAt`

**Tip**
- Customer tips for drivers
- Fields: `BookingId`, `CustomerId`, `DriverId`, `Amount`, `Message`, `TippedAt`

### Code Changes

**File**: `BeeLogistics.Modules.Sales/Domain/Booking.cs`

- New constructor supports multi-stop creation
- Legacy constructor kept for backward compatibility (marked `[Obsolete]`)
- New methods: `ConfirmDriver()`, `MarkDriverEnRoute()`, `MarkPickedUp()`, `MarkInTransit()`, `MarkCompleted()`, `Cancel()`
- Legacy methods marked `[Obsolete]` but still functional

---

## API Endpoint Changes

### New Endpoints

```
POST   /api/bookings/calculate-fare
GET    /api/bookings/{id}/available-drivers
POST   /api/bookings/{id}/select-driver
POST   /api/bookings/{id}/stops/{stopId}/complete
POST   /api/bookings/{id}/stops/{stopId}/pod
POST   /api/ratings
GET    /api/drivers/{id}/ratings
POST   /api/favourite-drivers
DELETE /api/favourite-drivers/{driverId}
GET    /api/drivers/earnings
```

### Modified Endpoints

**POST /api/bookings**
- **Request Body Changes:**
  - Old: Single `pickupLocation`, `dropoffLocation`
  - New: `stops` array (supports up to 20 stops)
  - New fields: `serviceType`, `scheduledDateTime`, `priorityFee`, `favouriteDriverId`
  - New field: `vehicleType` (renamed from `truckType`)

**GET /api/bookings/{id}**
- **Response Changes:**
  - Now includes `stops` array
  - Includes `proofOfDeliveries` array
  - Includes `estimatedFare`, `finalFare`, `distanceKm`
  - Legacy fields (`pickupLocation`, `dropoffLocation`) still included for backward compatibility

**POST /api/driver-offers/{id}/accept**
- **Changes:**
  - Faster expiration (30-60 seconds instead of 2-5 minutes)
  - Favourite driver priority (30-second timeout)
  - Response includes `driverRating`, `estimatedArrivalMinutes`

### Deprecated Endpoints

The following endpoints are **deprecated** and will be removed in a future version:

```
GET    /api/dispatches/*
POST   /api/dispatches/*
PUT    /api/dispatches/*
DELETE /api/dispatches/*

GET    /api/manifests/*
POST   /api/manifests/*
PUT    /api/manifests/*
DELETE /api/manifests/*

GET    /api/trucks/*
POST   /api/trucks/*
PUT    /api/trucks/*
DELETE /api/trucks/*

GET    /api/companies/*
POST   /api/companies/*
PUT    /api/companies/*
DELETE /api/companies/*
```

**Note**: `/api/companies/*` endpoints are kept for backoffice use only.

---

## Database Schema Changes

### New Tables

**sales.DeliveryStops**
```sql
CREATE TABLE sales."DeliveryStops" (
    "Id" UUID PRIMARY KEY,
    "BookingId" UUID NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "Address" VARCHAR(500) NOT NULL,
    "Latitude" NUMERIC(18,8),
    "Longitude" NUMERIC(18,8),
    "ContactName" VARCHAR(255),
    "ContactPhone" VARCHAR(50),
    "Notes" VARCHAR(1000),
    "Type" VARCHAR(20) NOT NULL,
    "Status" VARCHAR(20) NOT NULL,
    "ArrivedAt" TIMESTAMP,
    "CompletedAt" TIMESTAMP,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE,
    FOREIGN KEY ("BookingId") REFERENCES sales."Bookings"("Id") ON DELETE CASCADE
);
```

**sales.ProofOfDeliveries**
```sql
CREATE TABLE sales."ProofOfDeliveries" (
    "Id" UUID PRIMARY KEY,
    "BookingId" UUID NOT NULL,
    "StopId" UUID NOT NULL,
    "ImagePath" VARCHAR(500) NOT NULL,
    "SignaturePath" VARCHAR(500),
    "DeliveredAt" TIMESTAMP NOT NULL,
    "RecipientName" VARCHAR(255),
    "Notes" VARCHAR(1000),
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE,
    FOREIGN KEY ("BookingId") REFERENCES sales."Bookings"("Id") ON DELETE CASCADE,
    FOREIGN KEY ("StopId") REFERENCES sales."DeliveryStops"("Id") ON DELETE RESTRICT
);
```

**sales.FavouriteDrivers**
```sql
CREATE TABLE sales."FavouriteDrivers" (
    "Id" UUID PRIMARY KEY,
    "CustomerId" UUID NOT NULL,
    "DriverId" UUID NOT NULL,
    "AddedAt" TIMESTAMP NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE,
    UNIQUE ("CustomerId", "DriverId")
);
```

**sales.Tips**
```sql
CREATE TABLE sales."Tips" (
    "Id" UUID PRIMARY KEY,
    "BookingId" UUID NOT NULL UNIQUE,
    "CustomerId" UUID NOT NULL,
    "DriverId" UUID NOT NULL,
    "Amount" NUMERIC(10,2) NOT NULL,
    "Message" VARCHAR(500),
    "TippedAt" TIMESTAMP NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE,
    FOREIGN KEY ("BookingId") REFERENCES sales."Bookings"("Id") ON DELETE CASCADE
);
```

**rating.Ratings**
```sql
CREATE TABLE rating."Ratings" (
    "Id" UUID PRIMARY KEY,
    "BookingId" UUID NOT NULL UNIQUE,
    "DriverId" UUID NOT NULL,
    "CustomerId" UUID NOT NULL,
    "Stars" INTEGER NOT NULL CHECK ("Stars" >= 1 AND "Stars" <= 5),
    "Comment" VARCHAR(1000),
    "Category" VARCHAR(30),
    "RatedAt" TIMESTAMP NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE
);
```

**rating.DriverRatings**
```sql
CREATE TABLE rating."DriverRatings" (
    "Id" UUID PRIMARY KEY,
    "DriverId" UUID NOT NULL UNIQUE,
    "AverageRating" NUMERIC(3,2) NOT NULL DEFAULT 0,
    "TotalRatings" INTEGER NOT NULL DEFAULT 0,
    "FiveStarCount" INTEGER NOT NULL DEFAULT 0,
    "FourStarCount" INTEGER NOT NULL DEFAULT 0,
    "ThreeStarCount" INTEGER NOT NULL DEFAULT 0,
    "TwoStarCount" INTEGER NOT NULL DEFAULT 0,
    "OneStarCount" INTEGER NOT NULL DEFAULT 0,
    "LastUpdatedAt" TIMESTAMP NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "UpdatedAt" TIMESTAMP,
    "IsDeleted" BOOLEAN DEFAULT FALSE
);
```

### Modified Tables

**sales.Bookings**
- **New Columns:**
  - `VehicleType` VARCHAR(50) (renamed from `TruckType`)
  - `EstimatedFare` NUMERIC(10,2)
  - `FinalFare` NUMERIC(10,2)
  - `DistanceKm` NUMERIC(10,2)
  - `PriorityFee` NUMERIC(10,2)
  - `ServiceType` VARCHAR(20)
  - `ScheduledDateTime` TIMESTAMP
  - `ScheduledPickupWindow` VARCHAR(50)
  - `SelectedDriverId` UUID
  - `FavouriteDriverId` UUID

- **Deprecated Columns** (kept for backward compatibility):
  - `PickupLocation`, `DropoffLocation` - Use `DeliveryStops` instead
  - `TruckType` - Renamed to `VehicleType`
  - `Size` - No longer used
  - `AssignmentStatus` - Use `Status` instead
  - `AssignedToTenantId`, `AssignedByUserId`, `BeeTenantId` - No longer used

**sales.DriverBookingOffers**
- **New Columns:**
  - `IsFavouriteDriver` BOOLEAN
  - `DriverRating` NUMERIC(3,2)
  - `EstimatedArrivalMinutes` INTEGER

- **Deprecated Columns:**
  - `SequenceNumber` - No longer used

**identity.ApplicationUsers**
- **Deprecated Columns** (marked nullable, will be removed):
  - `CompanyId` - No fleet companies
  - `BusinessType` - No fleet vs individual distinction
  - `IsSoloDriver` - All drivers are independent

### Deprecated Tables

The following tables are **deprecated** and will be removed in a future version:

- `operations.Dispatches` - No longer used (direct driver assignment)
- `operations.Manifests` - No longer used
- `fleet.Trucks` - May be repurposed for vehicle type reference only

**Note**: `companies.Companies` table is kept for backoffice use only.

---

## Configuration Changes

### New Configuration Sections

**appsettings.json** - Added:

```json
{
  "Pricing": {
    "VehicleTypes": {
      "Motorcycle": { "BaseFare": 49, "PerKm0to5": 6, "PerKmAbove5": 5, ... },
      "Sedan": { "BaseFare": 100, "PerKm0to5": 18, "PerKmAbove5": 15, ... },
      ...
    },
    "HighDemandSurcharge": {
      "MaxMultiplier": 3.0,
      "PeakHours": [ "07:00-09:00", "17:00-19:00" ]
    },
    "CommissionRate": 0.20,
    "DriverEarningsRate": 0.80,
    "MinWithdrawalAmount": 20
  },
  "Matching": {
    "OfferExpirationSeconds": 60,
    "FavouriteDriverPrioritySeconds": 30,
    "SearchRadiusKm": 50
  }
}
```

### Removed Configuration

- `BookingSettings:SmallVehicleTruckTypes` - No longer used
- `BookingSettings:SmallPackageWeightThreshold` - No longer used
- `BookingSettings:OfferExpirationManyDriversMinutes` - Use `Matching:OfferExpirationSeconds` instead
- `BookingSettings:OfferExpirationFewDriversMinutes` - Use `Matching:OfferExpirationSeconds` instead

---

## Frontend Breaking Changes

### Component Removals

The following components/pages should be **removed** or **deprecated**:

- Fleet management pages
- Operator assignment UI
- Dispatch/manifest management
- Truck management (unless repurposed for vehicle types)

### Component Updates Required

**Customer App (`bee-frontend`):**
- `BookingForm.tsx` - Update to support multi-stop input
- `BookingManagement.tsx` - Remove fleet-related features
- Add: Driver selection screen
- Add: Favourite drivers management
- Add: Rating/review interface
- Add: POD viewing in booking history

**Driver App (`bee-drivers-app-v2`):**
- Update accept/reject flow (30-60s timer)
- Add: Multi-stop navigation
- Add: POD upload at each stop
- Update: Earnings display (fare + tips breakdown)
- Add: Rating display
- Remove: Fleet/operator dashboard features

### API Client Changes

**Updated Request/Response Types:**
- `BookingDto` - Now includes `stops`, `proofOfDeliveries`, `estimatedFare`, `finalFare`
- `CreateBookingDto` - Now requires `stops` array instead of single pickup/dropoff
- `DriverOfferDto` - Now includes `isFavouriteDriver`, `driverRating`, `estimatedArrivalMinutes`

**New Types:**
- `DeliveryStopDto`
- `ProofOfDeliveryDto`
- `FavouriteDriverDto`
- `RatingDto`
- `TipDto`
- `PricingResultDto`

---

## Migration Guide

### Step 1: Database Migration

1. **Backup database** before migration
2. Run Entity Framework migrations:
   ```bash
   dotnet ef migrations add LalamoveRedesign --context SalesDbContext
   dotnet ef migrations add RatingModule --context RatingDbContext
   dotnet ef database update
   ```

3. **Data Migration Scripts:**
   - Migrate existing bookings to use `DeliveryStops` table
   - Convert `Client` role to `Customer` role
   - Remove or convert `Owner`/`Admin`/`Dispatcher` users
   - Update booking statuses to new enum values

### Step 2: Code Updates

1. **Update Role Checks:**
   ```csharp
   // Old
   if (user.Role == UserRoles.Client || user.Role == UserRoles.BusinessClient)
   
   // New
   if (user.Role == UserRoles.Customer)
   ```

2. **Update Booking Creation:**
   ```csharp
   // Old
   var booking = new Booking(customerId, pickup, dropoff, truckType, ...);
   
   // New
   var stops = new List<DeliveryStop>
   {
       new DeliveryStop(bookingId, 0, pickupAddress, StopType.Pickup, lat, lon),
       new DeliveryStop(bookingId, 1, dropoffAddress, StopType.Dropoff, lat, lon)
   };
   var booking = new Booking(customerId, vehicleType, cargoDesc, scheduleDate, 
                             ServiceType.Immediate, estimatedFare, stops, ...);
   ```

3. **Update Status Checks:**
   ```csharp
   // Old
   if (booking.Status == BookingStatus.Dispatched)
   
   // New
   if (booking.Status == BookingStatus.DriverAssigned)
   ```

### Step 3: Configuration Updates

1. Update `appsettings.json` with new `Pricing` and `Matching` sections
2. Remove deprecated `BookingSettings` values
3. Update CORS origins if needed

### Step 4: Frontend Updates

1. Update API client to use new endpoints
2. Update booking forms to support multi-stop
3. Remove fleet management UI
4. Add new features: driver selection, ratings, POD upload

### Step 5: Testing

1. Test booking creation with multi-stop
2. Test driver matching and selection
3. Test pricing calculations
4. Test rating system
5. Test POD upload
6. Test earnings and payouts

### Step 6: Deployment

1. Deploy backend changes
2. Deploy database migrations
3. Deploy frontend changes
4. Monitor for errors and rollback if needed

---

## Rollback Plan

If issues occur after deployment:

1. **Database Rollback:**
   ```bash
   dotnet ef database update <previous-migration-name>
   ```

2. **Code Rollback:**
   - Revert to previous git commit
   - Restore previous `appsettings.json`

3. **Frontend Rollback:**
   - Deploy previous frontend version
   - Restore previous API client

---

## Support

For questions or issues during migration, contact the development team or refer to:
- API Documentation: `/swagger`
- Module READMEs: `docs/` directory
- Development Guide: `docs/DEVELOPMENT.md`

---

**Last Updated**: January 29, 2026
