# Driver Offer API Integration Documentation

This document describes how to integrate with the Driver Offer API endpoints for retrieving pending booking offers and accepting/rejecting them.

## Overview

The Driver Offer API allows drivers to:
- View pending booking offers (with full booking details and multi-stop information)
- Accept booking offers
- Reject booking offers

All endpoints require authentication with a valid JWT token containing the `Driver` role.

---

## Base URL

```
Development: http://localhost:5248
Production: https://api.yourdomain.com
```

---

## Authentication

All endpoints require Bearer token authentication:

```
Authorization: Bearer <your-jwt-token>
```

The JWT token must contain:
- `sub` or `NameIdentifier` claim with the driver's user ID (Guid)
- `role` claim with value `Driver`

---

## Endpoints

### 1. Get Pending Offers

Retrieve pending booking offers for the authenticated driver.

**Endpoint:** `GET /api/driver-offers/pending`

**Query Parameters:**
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | integer | No | 3 | Maximum number of offers to return (max: 10) |

**Ordering:** highest dispatch priority first, then oldest offer first. Dispatch
priority comes from the booking's `deliveryMode` — `OnDemand` (100), `Regular`
(50), `Pooling` (10) — so an On-Demand offer appears ahead of an older Regular
one. Within a mode the order is unchanged: oldest first.

Because `limit` is applied *after* ranking, a driver polling with `limit=3` while
On-Demand offers keep arriving may not see an older Regular offer until one of
them is taken or expires. Clients that need the full picture should raise `limit`
rather than assume the list is chronological.

**Request Example:**
```http
GET /api/driver-offers/pending?limit=3
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response: 200 OK**
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "bookingId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
    "driverId": "550e8400-e29b-41d4-a716-446655440000",
    "tenantId": "123e4567-e89b-12d3-a456-426614174000",
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
    "customerId": "8f4e1234-5678-90ab-cdef-123456789abc",
    "customerName": "John Doe",
    "vehicleType": "Closed Van",
    "cargoDescription": "Electronics and appliances",
    "scheduleDate": "2024-01-15T14:00:00Z",
    "bookingStatus": "Pending",
    "notes": "Handle with care",
    "weightKg": 25.5,
    "itemImagePath": "/uploads/item-image.jpg",
    "estimatedFare": 150.00,
    "finalFare": null,
    "distanceKmTotal": 12.5,
    "stops": [
      {
        "sequence": 0,
        "address": "123 Main Street, Makati City, Metro Manila",
        "type": "Pickup",
        "latitude": 14.5995,
        "longitude": 120.9842,
        "contactName": "John Doe",
        "contactPhone": "+639123456789",
        "notes": "Ring doorbell twice"
      },
      {
        "sequence": 1,
        "address": "456 Oak Avenue, Quezon City, Metro Manila",
        "type": "Dropoff",
        "latitude": 14.6042,
        "longitude": 120.9889,
        "contactName": "Jane Smith",
        "contactPhone": "+639987654321",
        "notes": "Leave at front desk"
      },
      {
        "sequence": 2,
        "address": "789 Pine Road, Pasig City, Metro Manila",
        "type": "Dropoff",
        "latitude": 14.6100,
        "longitude": 120.9920,
        "contactName": "Bob Johnson",
        "contactPhone": "+639555123456",
        "notes": null
      }
    ]
  }
]
```

**Response Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Offer ID |
| `bookingId` | Guid | Booking ID |
| `driverId` | Guid | Driver ID |
| `tenantId` | Guid? | Tenant/Company ID (nullable) |
| `status` | string | Offer status: `Pending`, `Accepted`, `Rejected`, `Expired` |
| `offeredAt` | DateTime | When the offer was created |
| `respondedAt` | DateTime? | When driver responded (null if pending) |
| `expiresAt` | DateTime | When the offer expires |
| `sequenceNumber` | int | Queue position (lower = closer driver) |
| `distanceKm` | decimal? | Distance from driver to pickup location |
| `isFavouriteDriver` | bool | Whether driver is customer's favourite |
| `driverRating` | decimal? | Driver's average rating |
| `estimatedArrivalMinutes` | int? | Estimated minutes to arrive at pickup |
| `bookingNumber` | string | Human-readable booking number |
| `customerId` | Guid | Customer ID |
| `customerName` | string | Customer name |
| `vehicleType` | string | Required vehicle type |
| `cargoDescription` | string | Description of cargo |
| `scheduleDate` | DateTime | Scheduled pickup date/time |
| `bookingStatus` | string | Booking status: `Pending`, `Confirmed`, `DriverAssigned`, etc. |
| `notes` | string? | Additional notes |
| `weightKg` | decimal? | Cargo weight in kilograms |
| `itemImagePath` | string? | Path to item image |
| `estimatedFare` | decimal | Estimated fare amount |
| `finalFare` | decimal? | Final fare (null until completed) |
| `distanceKmTotal` | decimal? | Total route distance |
| `stops` | array | Multi-stop route information |

**Stop Object Fields:**

| Field | Type | Description |
|-------|------|-------------|
| `sequence` | int | Stop sequence (0 = pickup, 1+ = dropoffs) |
| `address` | string | Full address |
| `type` | string | `Pickup` or `Dropoff` |
| `latitude` | decimal? | GPS latitude |
| `longitude` | decimal? | GPS longitude |
| `contactName` | string? | Contact person name |
| `contactPhone` | string? | Contact phone number |
| `notes` | string? | Stop-specific notes |

**Error Responses:**

**401 Unauthorized**
```json
{
  "error": "Driver ID not found in token"
}
```

**500 Internal Server Error**
```json
{
  "error": "An error occurred while processing your request"
}
```

---

### 2. Accept Offer

Accept a pending booking offer.

**Endpoint:** `POST /api/driver-offers/{offerId}/accept`

**Path Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `offerId` | Guid | Yes | The ID of the offer to accept |

**Request Example:**
```http
POST /api/driver-offers/3fa85f64-5717-4562-b3fc-2c963f66afa6/accept
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response: 200 OK**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "bookingId": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
  "driverId": "550e8400-e29b-41d4-a716-446655440000",
  "tenantId": "123e4567-e89b-12d3-a456-426614174000",
  "status": "Accepted",
  "offeredAt": "2024-01-15T10:00:00Z",
  "respondedAt": "2024-01-15T10:02:30Z",
  "expiresAt": "2024-01-15T10:05:00Z",
  "sequenceNumber": 1,
  "distanceKm": 5.2
}
```

**Error Responses:**

**400 Bad Request**
```json
{
  "error": "Offer is not pending"
}
```

**400 Bad Request**
```json
{
  "error": "Offer has expired"
}
```

**400 Bad Request**
```json
{
  "error": "Another driver has already accepted this booking"
}
```

**404 Not Found**
```json
{
  "error": "Offer not found"
}
```

**401 Unauthorized**
```json
{
  "error": "Offer does not belong to this driver"
}
```

---

### 3. Reject Offer

Reject a pending booking offer.

**Endpoint:** `POST /api/driver-offers/{offerId}/reject`

**Path Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `offerId` | Guid | Yes | The ID of the offer to reject |

**Request Example:**
```http
POST /api/driver-offers/3fa85f64-5717-4562-b3fc-2c963f66afa6/reject
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response: 200 OK**
```json
{
  "success": true
}
```

**Error Responses:**

**400 Bad Request**
```json
{
  "error": "Offer is not pending"
}
```

**404 Not Found**
```json
{
  "error": "Offer not found"
}
```

**401 Unauthorized**
```json
{
  "error": "Offer does not belong to this driver"
}
```

---

## Integration Examples

### JavaScript/TypeScript (React Native/Expo)

```typescript
const API_BASE_URL = 'https://api.yourdomain.com';

interface DriverOffer {
  id: string;
  bookingId: string;
  status: string;
  expiresAt: string;
  distanceKm: number;
  estimatedFare: number;
  stops: Array<{
    sequence: number;
    address: string;
    type: 'Pickup' | 'Dropoff';
    latitude?: number;
    longitude?: number;
  }>;
  // ... other fields
}

class DriverOfferService {
  private getAuthHeaders(token: string) {
    return {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json',
    };
  }

  async getPendingOffers(token: string, limit: number = 3): Promise<DriverOffer[]> {
    const response = await fetch(
      `${API_BASE_URL}/api/driver-offers/pending?limit=${limit}`,
      {
        method: 'GET',
        headers: this.getAuthHeaders(token),
      }
    );

    if (!response.ok) {
      throw new Error(`Failed to fetch offers: ${response.statusText}`);
    }

    return await response.json();
  }

  async acceptOffer(token: string, offerId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE_URL}/api/driver-offers/${offerId}/accept`,
      {
        method: 'POST',
        headers: this.getAuthHeaders(token),
      }
    );

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.error || 'Failed to accept offer');
    }
  }

  async rejectOffer(token: string, offerId: string): Promise<void> {
    const response = await fetch(
      `${API_BASE_URL}/api/driver-offers/${offerId}/reject`,
      {
        method: 'POST',
        headers: this.getAuthHeaders(token),
      }
    );

    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.error || 'Failed to reject offer');
    }
  }
}

// Usage
const service = new DriverOfferService();
const token = 'your-jwt-token';

// Get pending offers
const offers = await service.getPendingOffers(token, 3);
console.log(`Found ${offers.length} pending offers`);

// Accept an offer
try {
  await service.acceptOffer(token, offers[0].id);
  console.log('Offer accepted successfully');
} catch (error) {
  console.error('Failed to accept offer:', error);
}
```

### C# (.NET)

```csharp
using System.Net.Http.Json;
using System.Text.Json;

public class DriverOfferService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public DriverOfferService(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
    }

    public async Task<List<DriverOfferDto>> GetPendingOffersAsync(
        string token, 
        int limit = 3)
    {
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.GetAsync(
            $"{_baseUrl}/api/driver-offers/pending?limit={limit}");

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<DriverOfferDto>>() 
            ?? new List<DriverOfferDto>();
    }

    public async Task AcceptOfferAsync(string token, Guid offerId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/driver-offers/{offerId}/accept", 
            null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            throw new Exception(error?.Error ?? "Failed to accept offer");
        }
    }

    public async Task RejectOfferAsync(string token, Guid offerId)
    {
        _httpClient.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/driver-offers/{offerId}/reject", 
            null);

        response.EnsureSuccessStatusCode();
    }
}

// DTOs
public record DriverOfferDto(
    Guid Id,
    Guid BookingId,
    Guid DriverId,
    Guid? TenantId,
    string Status,
    DateTime OfferedAt,
    DateTime? RespondedAt,
    DateTime ExpiresAt,
    int SequenceNumber,
    decimal? DistanceKm,
    bool IsFavouriteDriver,
    decimal? DriverRating,
    int? EstimatedArrivalMinutes,
    string BookingNumber,
    Guid CustomerId,
    string CustomerName,
    string VehicleType,
    string CargoDescription,
    DateTime ScheduleDate,
    string BookingStatus,
    string? Notes,
    decimal? WeightKg,
    string? ItemImagePath,
    decimal? EstimatedFare,
    decimal? FinalFare,
    decimal? DistanceKmTotal,
    List<DeliveryStopDto> Stops
);

public record DeliveryStopDto(
    int Sequence,
    string Address,
    string Type,
    decimal? Latitude,
    decimal? Longitude,
    string? ContactName,
    string? ContactPhone,
    string? Notes
);

public record ErrorResponse(string Error);
```

### Python

```python
import requests
from typing import List, Optional
from datetime import datetime

class DriverOfferService:
    def __init__(self, base_url: str):
        self.base_url = base_url
    
    def get_pending_offers(self, token: str, limit: int = 3) -> List[dict]:
        """Get pending offers for the authenticated driver."""
        headers = {
            'Authorization': f'Bearer {token}',
            'Content-Type': 'application/json'
        }
        
        response = requests.get(
            f'{self.base_url}/api/driver-offers/pending',
            params={'limit': limit},
            headers=headers
        )
        
        response.raise_for_status()
        return response.json()
    
    def accept_offer(self, token: str, offer_id: str) -> dict:
        """Accept a pending offer."""
        headers = {
            'Authorization': f'Bearer {token}',
            'Content-Type': 'application/json'
        }
        
        response = requests.post(
            f'{self.base_url}/api/driver-offers/{offer_id}/accept',
            headers=headers
        )
        
        response.raise_for_status()
        return response.json()
    
    def reject_offer(self, token: str, offer_id: str) -> dict:
        """Reject a pending offer."""
        headers = {
            'Authorization': f'Bearer {token}',
            'Content-Type': 'application/json'
        }
        
        response = requests.post(
            f'{self.base_url}/api/driver-offers/{offer_id}/reject',
            headers=headers
        )
        
        response.raise_for_status()
        return response.json()

# Usage
service = DriverOfferService('https://api.yourdomain.com')
token = 'your-jwt-token'

# Get pending offers
offers = service.get_pending_offers(token, limit=3)
print(f"Found {len(offers)} pending offers")

# Accept first offer
if offers:
    result = service.accept_offer(token, offers[0]['id'])
    print("Offer accepted:", result)
```

---

## Best Practices

### 1. Polling Frequency

Poll the `/pending` endpoint every **5-10 seconds** for real-time updates:

```typescript
// Poll every 5 seconds
setInterval(async () => {
  const offers = await service.getPendingOffers(token);
  updateUI(offers);
}, 5000);
```

### 2. Handle Expired Offers

Always check `expiresAt` before displaying offers:

```typescript
const validOffers = offers.filter(offer => 
  new Date(offer.expiresAt) > new Date()
);
```

### 3. Error Handling

Implement retry logic for network errors:

```typescript
async function getOffersWithRetry(token: string, retries = 3) {
  for (let i = 0; i < retries; i++) {
    try {
      return await service.getPendingOffers(token);
    } catch (error) {
      if (i === retries - 1) throw error;
      await new Promise(resolve => setTimeout(resolve, 1000 * (i + 1)));
    }
  }
}
```

### 4. Multi-Stop Route Display

Use the `stops` array to display the complete route:

```typescript
function displayRoute(stops: DeliveryStop[]) {
  const pickup = stops.find(s => s.type === 'Pickup');
  const dropoffs = stops.filter(s => s.type === 'Dropoff');
  
  console.log(`Pickup: ${pickup?.address}`);
  dropoffs.forEach((stop, index) => {
    console.log(`Stop ${index + 1}: ${stop.address}`);
  });
}
```

### 5. Quick Response

Drivers should accept/reject within the expiration window (typically 2-5 minutes). Show a countdown timer:

```typescript
function getTimeRemaining(expiresAt: string): number {
  const expires = new Date(expiresAt);
  const now = new Date();
  return Math.max(0, Math.floor((expires.getTime() - now.getTime()) / 1000));
}
```

---

## Rate Limiting

The API implements rate limiting:
- **General endpoints**: 100 requests per 60 seconds
- **Auth endpoints**: 5 requests per 300 seconds

If rate limited, you'll receive:
```json
{
  "error": "Rate limit exceeded. Please try again later."
}
```

---

## Webhook Support (Future)

Webhook notifications for offer updates are planned for future releases. Subscribe to:
- `offer.created` - New offer available
- `offer.expired` - Offer expired
- `offer.accepted` - Offer accepted by another driver

---

## Support

For API support or questions:
- Check [DRIVER_OFFER_FLOW.md](./DRIVER_OFFER_FLOW.md) for business logic details
- Review [API_REFERENCE.md](./API_REFERENCE.md) for general API documentation
- Contact: api-support@beelogistics.com

