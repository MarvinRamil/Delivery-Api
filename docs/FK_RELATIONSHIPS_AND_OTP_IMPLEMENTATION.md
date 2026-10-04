# Foreign Key Relationships and OTP Workflow Implementation

## Overview

This document describes the implementation of Foreign Key (FK) relationships across all modules and the new OTP-first registration workflow. These changes improve data integrity and provide a more secure, user-friendly registration process.

## Date: January 30, 2026

---

## Part 1: Foreign Key Relationships

### Purpose

Previously, many entities across different modules (Drivers, Sales, Rating, CRM) had `UserId`, `DriverId`, or `CustomerId` fields that were not enforced as Foreign Keys. This created potential data integrity issues where:

- Orphaned records could exist (e.g., a `DriverApplication` referencing a deleted user)
- No referential integrity checks were performed
- Data inconsistencies could occur

### Changes Made

#### 1. Drivers Module (`BeeLogistics.Modules.Drivers`)

**Domain Model Updates:**
- `DriverApplication`: Added `UserId` property (nullable string, FK to `ApplicationUser.Id`)
- `DriverWallet`: Already had `DriverId` (Guid) - added FK constraint documentation
- `WithdrawalRequest`: Already had `DriverId` (Guid) - added FK constraint documentation
- `DriverMission`: Already had `DriverId` (Guid) - added FK constraint documentation

**DbContext Updates (`DriversDbContext.cs`):**
- Added `UserId` column configuration for `DriverApplication` with index
- Added composite index `(UserId, Status)` for efficient "my pending application" queries
- Added property documentation comments indicating FK relationships

**Controller Updates (`DriverApplicationsController.cs`):**
- Modified `Create` endpoint to set `UserId` when creating applications
- Updated existing application check to use `UserId` (with fallback to `Email` for backward compatibility)
- Added new `GET /api/driver-applications/my-application` endpoint to check user's application status

#### 2. Sales Module (`BeeLogistics.Modules.Sales`)

**DbContext Updates (`SalesDbContext.cs`):**
- `FavouriteDriver`: Added FK documentation for `CustomerId` (to `Customer.Id`) and `DriverId` (to `ApplicationUser.Id`)
- `Tip`: Added FK documentation and explicit FK constraint to `Booking`
- `DriverBookingOffer`: Added FK documentation for `DriverId` (to `ApplicationUser.Id`)
- `Booking`: Added FK documentation for `SelectedDriverId` and `FavouriteDriverId` (to `ApplicationUser.Id`)

#### 3. Rating Module (`BeeLogistics.Modules.Rating`)

**DbContext Updates (`RatingDbContext.cs`):**
- `Rating`: Added FK documentation for `DriverId` (to `ApplicationUser.Id`) and `CustomerId` (to `Customer.Id`)
- `DriverRating`: Added FK documentation for `DriverId` (to `ApplicationUser.Id`)

#### 4. CRM Module (`BeeLogistics.Modules.CRM`)

**DbContext Updates (`CrmDbContext.cs`):**
- `CustomerProfile`: Made `UserId` required and added FK documentation (to `ApplicationUser.Id`)

### Delete Behavior

All FK relationships use `DeleteBehavior.Restrict` to prevent accidental data loss:
- If a user is deleted, related records (applications, wallets, etc.) are not automatically deleted
- This prevents cascading deletes that could cause data loss
- Manual cleanup or soft-delete patterns should be used instead

### Type Considerations

**Important Note on Type Mismatch:**
- `ApplicationUser.Id` is a `string` (from Identity framework)
- Many domain models use `Guid` for `DriverId`/`CustomerId`
- FK constraints are documented, but actual database FK constraints require:
  - For `DriverApplication.UserId` (string): Direct FK to `ApplicationUser.Id` ✅
  - For `DriverId` (Guid): Conversion logic in migrations (Guid stored as uuid, ApplicationUser.Id as text)
  - For `CustomerId` (Guid): FK to `Customer.Id` (Guid) ✅

### Migration Instructions

**To create migrations for FK relationships:**

```powershell
# Drivers Module
dotnet ef migrations add AddForeignKeyRelationships --project src/Modules/BeeLogistics.Modules.Drivers --startup-project src/BeeLogistics.Api

# Sales Module (if needed)
dotnet ef migrations add AddForeignKeyRelationships --project src/Modules/BeeLogistics.Modules.Sales --startup-project src/BeeLogistics.Api

# Rating Module (if needed)
dotnet ef migrations add AddForeignKeyRelationships --project src/Modules/BeeLogistics.Modules.Rating --startup-project src/BeeLogistics.Api

# CRM Module (if needed)
dotnet ef migrations add AddForeignKeyRelationships --project src/Modules/BeeLogistics.Modules.CRM --startup-project src/BeeLogistics.Api
```

**Note:** Cross-DbContext FK constraints (e.g., `DriverApplication.UserId` → `ApplicationUser.Id`) may require raw SQL in migrations since they reference tables in different schemas/DbContexts.

**To apply migrations:**

Use the provided `migrate-all.ps1` script:
```powershell
.\migrate-all.ps1
```

Or manually:
```powershell
dotnet ef database update --context DriversDbContext --project src/Modules/BeeLogistics.Modules.Drivers --startup-project src/BeeLogistics.Api
```

---

## Part 2: OTP-First Registration Workflow

### Purpose

The previous registration flow required:
1. User registers → Account created
2. Email verification link sent
3. User clicks link → Email verified
4. User logs in → Redirected to complete registration

This flow had issues:
- Users had to manage email links
- Multiple steps created friction
- Email verification could be lost/expired

The new OTP-first flow:
1. User enters email → OTP sent
2. User enters OTP + registration details → Account created + auto-logged in
3. User redirected to complete registration (if driver)

### Changes Made

#### 1. OtpService Extension (`OtpService.cs`)

**New Methods:**
- `GenerateEmailVerificationOtpAsync(string email)`: Generates OTP for email verification (before account creation)
- `ValidateEmailVerificationOtpAsync(string email, string otp)`: Validates OTP with brute-force protection
- `IsEmailLockedAsync(string email)`: Checks if email is locked due to too many failed attempts
- `InvalidateEmailVerificationOtpAsync(string email)`: Invalidates existing OTP (for resend)

**Security Features:**
- Uses separate cache prefix: `"otp:email-verification:"`
- 10-minute expiration (configurable)
- Maximum 5 attempts before email lock (30-minute lock)
- Email normalization (lowercase, trim) to prevent bypasses

#### 2. Rate Limiting Middleware (`RateLimitingMiddleware.cs`)

**New Rate Limits:**
- **OTP Send**: 5 requests per email per 15 minutes + 10 requests per IP per hour
- **OTP Verify**: 10 attempts per email per 10 minutes
- **OTP Resend**: 3 requests per email per 15 minutes

**Email-Based Rate Limiting:**
- Extracts email from request body for OTP endpoints
- Uses email as cache key for per-email limits
- Falls back to IP-based limiting if email extraction fails
- Uses `EnableBuffering()` to read body without consuming it

#### 3. New API Endpoints (`AuthController.cs`)

**POST `/api/auth/send-otp`** (Anonymous)
- Sends OTP to email for verification
- **Security**: Generic response to prevent email enumeration
- **Rate Limited**: Per-email and per-IP limits

**POST `/api/auth/verify-otp-and-register`** (Anonymous)
- Verifies OTP and creates account in one step
- Auto-logs in user (returns JWT token + refresh token)
- Sets `EmailConfirmed = true` (OTP verification counts as email confirmation)
- Processes referral codes if provided
- **Security**: Validates OTP before account creation

**POST `/api/auth/resend-otp`** (Anonymous)
- Invalidates previous OTP and sends new one
- **Security**: Rate limited to prevent spam

**Request/Response Models:**
```csharp
public record SendOtpRequest(string Email);
public record VerifyOtpAndRegisterRequest(string Email, string Otp, string Password, string FullName, string? Role = null, string? ReferralCode = null);
public record ResendOtpRequest(string Email);
```

#### 4. Mobile App Updates

**`_layout.tsx`:**
- Added check for pending driver application before redirecting to complete-registration
- Calls `GET /api/driver-applications/my-application` to check application status
- Prevents redirect if application exists and is pending

**Note:** The mobile app registration flow should be updated to use the new OTP endpoints (`/api/auth/send-otp`, `/api/auth/verify-otp-and-register`) instead of the old `/api/auth/register` endpoint. This is a separate frontend task.

### Security Considerations

#### Email Enumeration Prevention
- All OTP endpoints return generic success messages
- No indication whether email exists or not
- Prevents attackers from discovering registered emails

#### Brute Force Protection
- Maximum 5 OTP verification attempts per email
- Email locked for 30 minutes after 5 failed attempts
- Rate limiting at both email and IP levels

#### Rate Limiting Strategy
- **Per-Email Limits**: Prevent spam to specific users
- **Per-IP Limits**: Prevent abuse from single IP
- **Progressive Lockouts**: Temporary locks after repeated failures

#### OTP Security
- 6-digit numeric OTP (1,000,000 possible combinations)
- 10-minute expiration
- Single-use (invalidated after successful verification)
- Stored in distributed cache (Redis) with expiration

---

## Part 3: Application Code Updates

### DriverApplicationsController Changes

1. **Create Endpoint:**
   - Now requires authentication (`[Authorize(Roles = UserRoles.Driver)]`)
   - Sets `UserId` when creating application
   - Checks for existing application by `UserId` (with email fallback)
   - Returns `existingApplicationId` and `status` if application exists

2. **New Endpoint:**
   - `GET /api/driver-applications/my-application`: Returns current user's application status

### Mobile App Flow

**Current Flow (to be updated):**
1. User signs up → Account created → Email verification link sent
2. User verifies email → Logs in → Redirected to complete registration

**New Flow (recommended):**
1. User enters email → `POST /api/auth/send-otp` → OTP sent
2. User enters OTP + details → `POST /api/auth/verify-otp-and-register` → Account created + auto-logged in
3. User redirected to complete registration (if driver)

---

## Migration Strategy

### Backward Compatibility

1. **DriverApplication.UserId**: Made nullable to support existing records
2. **Existing Application Check**: Falls back to email lookup if `UserId` is null
3. **Old Registration Endpoint**: Still available (`POST /api/auth/register`) for backward compatibility

### Data Migration

**For existing DriverApplication records:**
```sql
-- Update existing DriverApplication records to set UserId
UPDATE drivers."DriverApplications" da
SET "UserId" = au."Id"
FROM identity."AspNetUsers" au
WHERE da."Email" = au."Email"
AND da."UserId" IS NULL;
```

---

## Testing Checklist

### FK Relationships
- [ ] Verify `DriverApplication.UserId` is set when creating application
- [ ] Verify `GET /api/driver-applications/my-application` returns correct application
- [ ] Test that users with pending applications are not redirected to complete-registration
- [ ] Verify FK constraints prevent orphaned records (if database FKs are added)

### OTP Workflow
- [ ] Test `POST /api/auth/send-otp` with valid email
- [ ] Test `POST /api/auth/send-otp` with invalid email (should return generic success)
- [ ] Test `POST /api/auth/verify-otp-and-register` with valid OTP
- [ ] Test `POST /api/auth/verify-otp-and-register` with invalid OTP (should fail)
- [ ] Test rate limiting (5 OTP sends per email per 15 minutes)
- [ ] Test brute force protection (5 failed attempts locks email)
- [ ] Test `POST /api/auth/resend-otp` invalidates previous OTP
- [ ] Verify auto-login after OTP registration

### Security
- [ ] Verify email enumeration prevention (generic responses)
- [ ] Verify rate limiting works (per-email and per-IP)
- [ ] Verify OTP expiration (10 minutes)
- [ ] Verify OTP single-use (invalidated after verification)

---

## Future Improvements

1. **Database FK Constraints**: Add actual FK constraints in database (requires raw SQL in migrations for cross-schema FKs)
2. **Mobile App OTP Flow**: Update mobile app to use new OTP endpoints
3. **SMS OTP**: Add SMS OTP option for phone number verification
4. **OTP Resend Cooldown**: Add UI indication of resend cooldown period
5. **Account Recovery**: Use OTP for password reset flow

---

## Files Modified

### Backend
- `src/Modules/BeeLogistics.Modules.Drivers/Domain/DriverApplication.cs`
- `src/Modules/BeeLogistics.Modules.Drivers/Infrastructure/DriversDbContext.cs`
- `src/Modules/BeeLogistics.Modules.Drivers/Presentation/Controllers/DriverApplicationsController.cs`
- `src/Modules/BeeLogistics.Modules.Sales/Infrastructure/SalesDbContext.cs`
- `src/Modules/BeeLogistics.Modules.Rating/Infrastructure/RatingDbContext.cs`
- `src/Modules/BeeLogistics.Modules.CRM/Infrastructure/CrmDbContext.cs`
- `src/Modules/BeeLogistics.Modules.Identity/Application/Services/OtpService.cs`
- `src/BeeLogistics.Api/Middleware/RateLimitingMiddleware.cs`
- `src/Modules/BeeLogistics.Modules.Identity/Presentation/Controllers/AuthController.cs`

### Mobile App
- `app/_layout.tsx`

---

## Summary

This implementation adds:
1. **Data Integrity**: FK relationships documented and enforced where possible
2. **Better UX**: OTP-first registration flow reduces friction
3. **Security**: Rate limiting, brute force protection, email enumeration prevention
4. **Backward Compatibility**: Existing flows continue to work

The changes are designed to be non-breaking and can be deployed incrementally.
