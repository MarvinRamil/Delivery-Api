# Module Removal Recommendations for Lalamove Model

## Summary

Based on the Lalamove-style redesign, the following modules can be **removed or deprecated** since they represent fleet-based concepts that are no longer needed:

## ✅ Modules to Remove/Deprecate

### 1. **Operations Module** ❌ REMOVE
**Location**: `src/Modules/BeeLogistics.Modules.Operations/`

**Why Remove:**
- Contains `Dispatch` and `Manifest` entities
- These are fleet-based concepts for grouping bookings into truck routes
- In Lalamove model: Direct driver matching, no dispatches/manifests needed
- Bookings go directly from customer → driver acceptance → delivery

**Current Usage:**
- Referenced in `Sales` module (`DriverAvailabilityService` checks for active dispatches)
- `BookingHandlers` syncs dispatch status
- Frontend has `DispatchManagement` and `ManifestManagement` components

**Migration Steps:**
1. Remove dispatch status checks from `DriverAvailabilityService`
2. Remove dispatch sync logic from `BookingHandlers`
3. Remove Operations module registration from `Program.cs`
4. Remove Operations migrations from `DbSeeder.cs`
5. Remove Operations from MediatR registration
6. **Keep database tables** (don't drop) - mark as deprecated for historical data

### 2. **Fleet Module** ⚠️ DEPRECATE (or repurpose)
**Location**: `src/Modules/BeeLogistics.Modules.Fleet/`

**Why Deprecate:**
- Contains `Truck` entity (fleet-owned vehicles)
- In Lalamove: Drivers use their own vehicles
- Vehicle types should be **configuration**, not entities

**Recommendation:**
- **Option A**: Remove entirely if vehicle types are handled in config
- **Option B**: Repurpose for vehicle type management (configuration only, no fleet ownership)
- Keep `TruckType` enum/configuration if needed for pricing

**Current Usage:**
- Referenced in pricing service for vehicle types
- Frontend has truck management UI

**Migration Steps:**
1. Move vehicle type configuration to `appsettings.json` or separate config service
2. Remove truck ownership concepts
3. Keep vehicle type enum for pricing if needed
4. Remove Fleet module if repurposing not needed

### 3. **Company Module** ⚠️ KEEP FOR BACKOFFICE ONLY
**Location**: `src/Modules/BeeLogistics.Modules.Company/`

**Why Keep (but limit):**
- Still needed for backoffice/admin management
- Multi-tenant support may be needed for platform management
- **Remove from main app flow** - only backoffice should access

**Recommendation:**
- Keep module but restrict access to `SuperAdmin` role only
- Remove from customer/driver flows
- Use only for platform administration

## ✅ Modules to Keep

### Core Modules (Essential):
- ✅ **Identity** - User authentication/authorization
- ✅ **Sales** - Bookings, customers, driver offers, pricing
- ✅ **Drivers** - Driver profiles, wallets, earnings
- ✅ **Rating** - Customer ratings and driver rating aggregation ✨ NEW
- ✅ **Payment** - Payment processing
- ✅ **Chat** - Customer-driver communication
- ✅ **Notification** - Push notifications, emails
- ✅ **Map** - Location tracking, geocoding
- ✅ **Referrals** - Referral system
- ✅ **CRM** - Support tickets, FAQs

## Migration Checklist

### Phase 1: Remove Operations Module References
- [ ] Remove `HasActiveDispatchAsync` check from `DriverAvailabilityService.cs`
- [ ] Remove dispatch sync logic from `BookingHandlers.cs` (lines 301-340)
- [ ] Remove `GetCustomersFromDispatchesQueryHandler` from `BookingHandlers.cs`
- [ ] Remove Operations module from `Program.cs` (line 329)
- [ ] Remove Operations migrations from `DbSeeder.cs`
- [ ] Remove Operations from MediatR registration (line 417)

### Phase 2: Deprecate Fleet Module
- [ ] Move vehicle type configuration to `appsettings.json`
- [ ] Update pricing service to use config instead of Truck entities
- [ ] Remove truck ownership from driver profiles
- [ ] Optionally remove Fleet module entirely

### Phase 3: Restrict Company Module
- [ ] Add `[Authorize(Roles = "SuperAdmin")]` to CompaniesController
- [ ] Remove company references from customer/driver flows
- [ ] Keep only for backoffice admin panel

### Phase 4: Frontend Cleanup
- [ ] Remove `DispatchManagement.tsx` component
- [ ] Remove `ManifestManagement.tsx` component
- [ ] Remove dispatch/manifest API hooks
- [ ] Remove fleet management UI
- [ ] Update booking flow to remove dispatch assignment

## Database Considerations

**⚠️ IMPORTANT**: Do NOT drop database tables immediately:
1. Keep tables for historical data
2. Mark as deprecated in code comments
3. Create migration to add `IsDeprecated` flag if needed
4. Plan data archival strategy before dropping

## Current Status

✅ **Rating Module**: Complete and ready
- Domain entities: `Rating`, `DriverRating`
- Repositories: `IRatingRepository`, `IDriverRatingRepository`
- Application layer: Commands, Queries, Handlers, DTOs
- Presentation layer: `RatingsController` with full CRUD
- Database: Migrations created and applied

## Next Steps

1. **Remove Operations module** (highest priority - not needed)
2. **Update Sales module** to remove dispatch checks
3. **Deprecate Fleet module** or repurpose for config
4. **Restrict Company module** to backoffice only
5. **Update frontend** to remove dispatch/manifest UI

## Notes

- The Lalamove model is **simpler**: Customer → Booking → Driver → Delivery
- No intermediate concepts like dispatches/manifests needed
- Direct driver matching replaces fleet assignment
- Vehicle types are configuration, not entities
