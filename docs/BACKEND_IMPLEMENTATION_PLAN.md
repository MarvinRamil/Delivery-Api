# Backend Implementation Plan - Driver App Endpoints & Referral System

## Overview

This document outlines the plan to:
1. Add missing backend endpoints for driver app
2. Implement referral system for drivers and customers
3. Consider WebSocket vs MQTT for location tracking

---

## Part 1: WebSocket vs MQTT Analysis

### Current Implementation
- **Backend**: Uses MQTT over WebSocket (WSS) - `mqtt.gregdoesdev.xyz:80/mqtt`
- **Driver App**: Uses `react-native-paho-mqtt` (MQTT over WebSocket)

### Recommendation: **Keep MQTT over WebSocket**

**Why MQTT is Better:**
1. ✅ **Built-in Features**: QoS levels, retained messages, last will & testament
2. ✅ **Topic-based Routing**: `drivers/{driverId}/geo` - clean, scalable
3. ✅ **Backend Already Configured**: MQTT subscriber service is working
4. ✅ **Message Persistence**: MQTT handles offline messages better
5. ✅ **Standard Protocol**: Industry standard for IoT/location tracking
6. ✅ **Already Using WS**: MQTT over WebSocket = best of both worlds

**Pure WebSocket Would Require:**
- Custom message protocol
- Manual reconnection logic
- No built-in QoS guarantees
- More complex error handling

**Verdict**: ✅ **Keep MQTT over WebSocket** - it's already optimal!

---

## Part 2: Referral System Design

### Design Decisions Needed

#### 1. Referral Link Strategy

**Option A: Single Permanent Link (Recommended)**
- ✅ One referral link per user (never changes)
- ✅ Easy to share, remember, print on business cards
- ✅ Simple tracking: `referralCode` = user identifier
- ✅ Format: `https://bee.app/ref/{userId}` or `https://bee.app/ref/{code}`

**Option B: Regenerable Links**
- ❌ Can be confusing (which link is active?)
- ❌ Harder to track (multiple links per user)
- ❌ Users might lose track

**Recommendation**: **Option A - Single Permanent Link**

#### 2. Referral Relationship Model

**Option A: 1-to-Many (Recommended)**
- ✅ One referrer can have unlimited referrals
- ✅ More scalable and flexible
- ✅ Better for viral growth
- ✅ Can track: "Referred by User X" for each new user

**Option B: 1-to-1**
- ❌ Limits growth potential
- ❌ Complex to manage
- ❌ Less incentive to refer

**Recommendation**: **Option A - 1-to-Many**

#### 3. Points/Rewards Structure

**For Drivers:**
- Refer another driver → Points when they complete first 5 deliveries
- Refer a customer → Points when customer makes first booking

**For Customers:**
- Refer another customer → Points when they make first booking
- Refer a driver → Points when driver completes first 5 deliveries

**Points Value:**
- Configurable per referral type
- Can be converted to cash or used for discounts

---

## Part 3: Database Models & Migrations

### 3.1 Referral System Models

#### ReferralCode (Domain Entity)
```csharp
public class ReferralCode : Entity
{
    public Guid UserId { get; private set; } // Driver or Customer
    public string Code { get; private set; } // Unique referral code
    public ReferralUserType UserType { get; private set; } // Driver or Customer
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    
    // Navigation
    public ApplicationUser User { get; private set; }
    public IReadOnlyCollection<Referral> Referrals { get; private set; }
}
```

#### Referral (Domain Entity)
```csharp
public class Referral : Entity
{
    public Guid ReferralCodeId { get; private set; }
    public Guid ReferrerId { get; private set; } // Who referred
    public Guid ReferredUserId { get; private set; } // Who was referred
    public ReferralUserType ReferrerType { get; private set; } // Driver or Customer
    public ReferralUserType ReferredType { get; private set; } // Driver or Customer
    public ReferralStatus Status { get; private set; } // Pending, Completed, Rewarded
    public DateTime ReferredAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? RewardedAt { get; private set; }
    public decimal PointsAwarded { get; private set; }
    
    // Navigation
    public ReferralCode ReferralCode { get; private set; }
    public ApplicationUser Referrer { get; private set; }
    public ApplicationUser ReferredUser { get; private set; }
}
```

#### PointsTransaction (Domain Entity)
```csharp
public class PointsTransaction : Entity
{
    public Guid UserId { get; private set; }
    public PointsTransactionType Type { get; private set; } // Referral, Conversion, Redemption
    public decimal Points { get; private set; } // Positive for earned, negative for spent
    public decimal BalanceAfter { get; private set; }
    public string Description { get; private set; }
    public Guid? RelatedReferralId { get; private set; }
    public DateTime TransactionDate { get; private set; }
}
```

#### UserPoints (Domain Entity)
```csharp
public class UserPoints : Entity
{
    public Guid UserId { get; private set; }
    public decimal TotalPoints { get; private set; }
    public decimal AvailablePoints { get; private set; } // Can be redeemed
    public decimal PendingPoints { get; private set; } // Awaiting completion
    public DateTime LastUpdatedAt { get; private set; }
    
    // Navigation
    public ApplicationUser User { get; private set; }
}
```

### 3.2 Driver App Models

#### DriverWallet (Domain Entity)
```csharp
public class DriverWallet : Entity
{
    public Guid DriverId { get; private set; }
    public decimal Balance { get; private set; }
    public decimal PendingPayout { get; private set; }
    public string? BankAccountNumber { get; private set; }
    public string? BankName { get; private set; }
    public string? AccountHolderName { get; private set; }
    public DateTime LastUpdatedAt { get; private set; }
    
    // Navigation
    public ApplicationUser Driver { get; private set; }
    public IReadOnlyCollection<WalletTransaction> Transactions { get; private set; }
}
```

#### WalletTransaction (Domain Entity)
```csharp
public class WalletTransaction : Entity
{
    public Guid WalletId { get; private set; }
    public WalletTransactionType Type { get; private set; } // Earning, Withdrawal, Payout
    public decimal Amount { get; private set; }
    public WalletTransactionStatus Status { get; private set; } // Completed, Pending, Failed
    public string Description { get; private set; }
    public Guid? RelatedDispatchId { get; private set; }
    public Guid? RelatedWithdrawalRequestId { get; private set; }
    public DateTime TransactionDate { get; private set; }
}
```

#### WithdrawalRequest (Domain Entity)
```csharp
public class WithdrawalRequest : Entity
{
    public Guid DriverId { get; private set; }
    public Guid WalletId { get; private set; }
    public decimal Amount { get; private set; }
    public WithdrawalStatus Status { get; private set; } // Pending, Approved, Rejected, Completed
    public string BankAccountNumber { get; private set; }
    public string BankName { get; private set; }
    public string AccountHolderName { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public Guid? ProcessedByUserId { get; private set; }
}
```

#### DriverEarnings (View/Query Model)
```csharp
// This can be a computed view or query result
public class DriverEarnings
{
    public decimal Today { get; set; }
    public decimal ThisWeek { get; set; }
    public decimal ThisMonth { get; set; }
    public decimal Total { get; set; }
    public IReadOnlyList<EarningsBreakdown> Breakdown { get; set; }
}

public class EarningsBreakdown
{
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public int BookingsCount { get; set; }
}
```

#### DriverMission (Domain Entity)
```csharp
public class DriverMission : Entity
{
    public Guid DriverId { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public decimal Reward { get; private set; }
    public int Progress { get; private set; }
    public int Target { get; set; }
    public MissionStatus Status { get; private set; } // Active, Available, Completed, Expired
    public MissionType Type { get; private set; } // Weekly, Daily, Special
    public DateTime ExpiresAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? RewardedAt { get; private set; }
}
```

---

## Part 4: Module Structure

### New Module: `BeeLogistics.Modules.Referrals`

```
BeeLogistics.Modules.Referrals/
├── Domain/
│   ├── ReferralCode.cs
│   ├── Referral.cs
│   ├── UserPoints.cs
│   └── PointsTransaction.cs
├── Application/
│   ├── DTOs/
│   │   ├── ReferralCodeDto.cs
│   │   ├── ReferralDto.cs
│   │   ├── UserPointsDto.cs
│   │   └── PointsTransactionDto.cs
│   ├── Handlers/
│   │   ├── ReferralHandlers.cs
│   │   └── PointsHandlers.cs
│   └── Interfaces/
│       └── IReferralRepository.cs
├── Infrastructure/
│   ├── ReferralsDbContext.cs
│   ├── Repositories/
│   │   └── ReferralRepository.cs
│   └── Services/
│       └── QrCodeService.cs (for QR generation)
└── Presentation/
    └── Controllers/
        └── ReferralsController.cs
```

### Extend Existing Module: `BeeLogistics.Modules.Drivers`

Add to existing Drivers module:
- DriverWallet
- WalletTransaction
- WithdrawalRequest
- DriverEarnings (computed)
- DriverMission

---

## Part 5: API Endpoints

### 5.1 Driver Endpoints (Solo Drivers)

#### Earnings
```
GET /api/drivers/{driverId}/earnings
Query: ?startDate=&endDate=&groupBy=day|week|month
Response: DriverEarningsDto
```

#### Wallet
```
GET /api/drivers/{driverId}/wallet
Response: DriverWalletDto

GET /api/drivers/{driverId}/wallet/transactions
Query: ?startDate=&endDate=&type=
Response: List<WalletTransactionDto>

POST /api/drivers/{driverId}/wallet/withdraw
Body: { amount, bankAccountNumber, bankName, accountHolderName }
Response: WithdrawalRequestDto
```

#### Missions
```
GET /api/drivers/{driverId}/missions
Query: ?status=active|available|completed
Response: List<DriverMissionDto>

POST /api/drivers/{driverId}/missions/{missionId}/claim
Response: DriverMissionDto
```

### 5.2 Referral Endpoints (Both Drivers & Customers)

```
GET /api/referrals/my-code
Response: ReferralCodeDto (with QR code URL)

GET /api/referrals/my-referrals
Query: ?status=pending|completed|rewarded
Response: List<ReferralDto>

GET /api/referrals/my-points
Response: UserPointsDto

GET /api/referrals/points/transactions
Query: ?startDate=&endDate=
Response: List<PointsTransactionDto>

POST /api/referrals/redeem
Body: { points, conversionType }
Response: PointsTransactionDto
```

### 5.3 Referral Registration Flow

```
POST /api/auth/register
Body: {
  ...existing fields...,
  referralCode?: string  // Optional referral code
}
```

When user registers with referral code:
1. Validate referral code exists and is active
2. Create Referral record (Status: Pending)
3. Track completion criteria (first booking, first 5 deliveries, etc.)
4. Award points when criteria met

---

## Part 6: Implementation Phases

### Phase 1: Database Models & Migrations
1. Create ReferralCode, Referral, UserPoints, PointsTransaction tables
2. Create DriverWallet, WalletTransaction, WithdrawalRequest tables
3. Create DriverMission table
4. Add indexes for performance

### Phase 2: Domain Layer
1. Implement domain entities with business logic
2. Add validation methods
3. Add state transition methods

### Phase 3: Application Layer
1. Create DTOs
2. Create Handlers (CQRS)
3. Create Repositories

### Phase 4: Infrastructure Layer
1. Implement repositories
2. Create DbContext
3. Add QR code generation service

### Phase 5: Presentation Layer
1. Create Controllers
2. Add authorization
3. Add validation

### Phase 6: Integration
1. Integrate referral code in registration
2. Add points awarding logic
3. Add wallet transaction creation

---

## Part 7: Referral System Business Rules

### Referral Completion Criteria

**Driver → Driver:**
- Referred driver completes first 5 deliveries
- Points awarded to referrer

**Driver → Customer:**
- Referred customer makes first booking
- Points awarded to driver

**Customer → Customer:**
- Referred customer makes first booking
- Points awarded to referrer customer

**Customer → Driver:**
- Referred driver completes first 5 deliveries
- Points awarded to customer

### Points Configuration
- Configurable per referral type
- Stored in configuration or database
- Can be updated without code changes

### QR Code Generation
- Generate QR code for referral link
- Store QR code image URL
- Support regeneration if needed (optional)

---

## Final Design Decisions ✅

### Points System
- **1 successful referral/registration = 10 points** (all types)
- **100 points = ₱50 discount** (2 points = ₱1)
- Points can be used for discounts on bookings/payments

### QR Code Strategy (Best Practice)
**Recommendation: Generate on-demand with caching**

**Why:**
- ✅ **Storage Efficient**: Don't store images in database
- ✅ **Flexible**: Can regenerate if needed (though link is permanent)
- ✅ **Performance**: Cache generated QR codes in memory/CDN
- ✅ **Scalable**: Generate only when requested

**Implementation:**
- Store referral link URL in database
- Generate QR code image on API request
- Cache in memory (5-10 min) or CDN (longer)
- Use library like `QRCoder` (C#) or `ZXing.Net`

### Referral Expiry
**No expiry needed** - referrals are permanent once completed
- Track `ReferredAt` date for analytics
- No expiry fields needed
- Referrals remain valid indefinitely

### Wallet Module Location
**Recommendation: Extend `BeeLogistics.Modules.Drivers`**

**Why:**
- ✅ **Cohesion**: Wallet is driver-specific (earnings, withdrawals)
- ✅ **Simplicity**: Keep related functionality together
- ✅ **Scalability**: Can refactor to shared module later if customers get wallets
- ✅ **Consistency**: Follows existing module pattern

**Alternative Consideration:**
- If customers get wallets later, we can:
  1. Create `BeeLogistics.Modules.Wallet` (shared)
  2. Or keep separate: `DriverWallet` in Drivers, `CustomerWallet` in Sales/CRM

**For now: Extend Drivers module** ✅

---

## Implementation Summary

✅ **MQTT over WebSocket**: Keep as-is (already optimal)
✅ **Referral Link**: Single permanent link per user
✅ **Referral Model**: 1-to-many (unlimited referrals)
✅ **Points**: 10 points per successful referral
✅ **Conversion**: 100 points = ₱50 (2 points = ₱1)
✅ **QR Code**: Generate on-demand with caching
✅ **Referral Expiry**: None (permanent)
✅ **Module**: Create new `BeeLogistics.Modules.Referrals`
✅ **Wallet**: Extend `BeeLogistics.Modules.Drivers`

**Ready to implement!** 🚀

