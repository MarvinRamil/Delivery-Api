# Lalamove-Style Multi-Step Booking Flow

This document describes how to create a booking using the multi-stop Lalamove-style API: **calculate fare** → **create booking** → (optional) **select driver**.

**Base URL:** Your API base (e.g. `https://your-api.com`)  
**Auth:** All requests require `Authorization: Bearer <token>`.

---

## Overview

| Step | Endpoint | Purpose |
|------|----------|--------|
| 1 | `POST /api/bookings/calculate-fare` | Get estimated fare before creating |
| 2 | `POST /api/bookings/lalamove` | Create booking with stops and estimated fare |
| 3 (optional) | `GET /api/bookings/{id}/available-drivers` | List drivers for booking |
| 4 (optional) | `POST /api/bookings/{id}/select-driver` | Select a driver |

**Fare flow:** Call **calculate-fare once** with **all stops** (pickup + dropoffs). The API returns **one total fare** for the entire route. Do **not** call calculate-fare per stop.

---

## Step 1: Calculate Fare

**Endpoint:** `POST /api/bookings/calculate-fare`  
**Content-Type:** `application/json`

### Request Body

```json
{
  "vehicleType": "Van",
  "stops": [
    {
      "sequence": 0,
      "address": "123 Pickup St",
      "type": "Pickup",
      "latitude": 14.5,
      "longitude": 121.0
    },
    {
      "sequence": 1,
      "address": "456 Dropoff St",
      "type": "Dropoff",
      "latitude": 14.6,
      "longitude": 121.1
    }
  ],
  "weightKg": 10,
  "priorityFee": 50,
  "scheduledDateTime": "2026-02-01T10:00:00Z"
}
```

### Fields

| Field | Type | Required | Notes |
|-------|------|----------|--------|
| vehicleType | string | Yes | e.g. Van, Truck |
| stops | array | Yes | 1–20 stops; exactly one `type: "Pickup"` |
| weightKg | number | No | |
| priorityFee | number | No | |
| scheduledDateTime | string (ISO) | No | For scheduled deliveries |

### Stop Object (in `stops` array)

| Field | Type | Required | Notes |
|-------|------|----------|--------|
| sequence | number | Yes | 0-based order |
| address | string | Yes | Full address |
| type | string | Yes | `"Pickup"` or `"Dropoff"` |
| latitude | number | No | -90 to 90 |
| longitude | number | No | -180 to 180 |
| contactName | string | No | |
| contactPhone | string | No | |
| notes | string | No | |

### Rules

- At least 1 stop; maximum 20 stops.
- **Exactly one pickup:** one stop must have `type: "Pickup"` (multi-stop is for **destinations only**, not multiple pickups).
- **Multiple dropoffs:** remaining stops use `type: "Dropoff"` (0–19 destinations).

### Response (200 OK)

```json
{
  "success": true,
  "data": {
    "baseFare": 100,
    "distanceFare": 80,
    "multiStopFee": 20,
    "weightSurcharge": 10,
    "priorityFee": 50,
    "highDemandSurcharge": 0,
    "tollFee": 0,
    "totalFare": 260,
    "distanceKm": 5.2,
    "breakdown": null
  }
}
```

**Use `data.totalFare`** (and optionally `distanceKm`, `breakdown`) when creating the booking in Step 2.

---

## Step 2: Create Booking

**Endpoint:** `POST /api/bookings/lalamove`  
**Content-Type:** `application/json`

### Request Body

```json
{
  "vehicleType": "Van",
  "cargoDescription": "Documents",
  "scheduleDate": "2026-02-01T00:00:00Z",
  "serviceType": "Immediate",
  "stops": [
    {
      "sequence": 0,
      "address": "123 Pickup St",
      "type": "Pickup",
      "latitude": 14.5,
      "longitude": 121.0,
      "contactName": "John",
      "contactPhone": "+639171234567"
    },
    {
      "sequence": 1,
      "address": "456 Dropoff St",
      "type": "Dropoff",
      "latitude": 14.6,
      "longitude": 121.1,
      "contactName": "Jane",
      "contactPhone": "+639179876543"
    }
  ],
  "estimatedFare": 260,
  "weightKg": 10,
  "priorityFee": 50,
  "notes": "Handle with care"
}
```

### Required Fields

| Field | Type | Notes |
|-------|------|--------|
| customerId | string (GUID) | Use `"00000000-0000-0000-0000-000000000000"` to use the logged-in user as customer; or send a specific customer GUID |
| vehicleType | string | e.g. Van, Truck |
| cargoDescription | string | Description of cargo |
| scheduleDate | string (ISO) | Date/time for the booking |
| serviceType | string | `"Immediate"` or `"Scheduled"` |
| stops | array | Same structure as Step 1; 1–20 stops, one Pickup |
| estimatedFare | number | Use `totalFare` from Step 1 |

### Optional Fields

| Field | Type | Notes |
|-------|------|--------|
| weightKg | number | |
| priorityFee | number | |
| scheduledDateTime | string (ISO) | For scheduled service |
| scheduledPickupWindow | string | |
| favouriteDriverId | string (GUID) | Preferred driver |
| itemImagePath | string | |
| notes | string | |

### Response (200 OK)

```json
{
  "success": true,
  "data": {
    "id": "guid",
    "bookingNumber": "BKG-...",
    "customerId": "guid",
    "customerName": "...",
    "pickupLocation": "...",
    "dropoffLocation": "...",
    "truckType": "Van",
    "cargoDescription": "Documents",
    "scheduleDate": "...",
    "status": "Pending",
    "notes": "...",
    "createdAt": "...",
    "updatedAt": null,
    "itemImagePath": null
  }
}
```

Save `data.id` for Step 3 (select driver).

**Note:** The response is the standard `BookingDto` (legacy shape: `pickupLocation`, `dropoffLocation`, `truckType`, etc.). It does **not** include a `stops` array; the backend stores stops but returns the legacy DTO for compatibility.

---

## Customer resolution & "Customer not found" (booking error)

The **"Customer not found"** message comes from the **booking** API (`POST /api/bookings/lalamove`), not from auth. It is returned when the backend cannot resolve which customer to attach to the booking.

### How customer is resolved (in the booking handler)

1. **Backend reads the JWT** for the current user:
   - `userEmail` = `User.FindFirstValue(ClaimTypes.Email)`
   - `userFullName` = `User.FindFirstValue(ClaimTypes.Name)`

2. **If `userEmail` is present (from the JWT):**
   - Handler looks up a **Customer** by that email.
   - If a customer exists → that customer is used for the booking.
   - If no customer exists → a **new Customer** is created with that email (and `userFullName` or email prefix as name), then used for the booking.
   - **"Customer not found" is not returned** in this path.

3. **If `userEmail` is null/empty (e.g. JWT has no email claim):**
   - Handler uses **`dto.CustomerId`** from the request body.
   - It looks up a customer by that GUID in the **Customers** table.
   - If **no customer exists** with that ID → the handler returns **"Customer not found"**.

So **"Customer not found"** happens when:
- The JWT does **not** include the email claim, so the backend uses `customerId` from the body, and
- That `customerId` is either missing, or not a valid GUID of an existing customer in the database.

### What to do

- **Preferred (backend):** Ensure the JWT issued at login includes the user’s **email** (and optionally **name**) so the booking handler can resolve or create the customer by email. Then the frontend can send `customerId: "00000000-0000-0000-0000-000000000000"` and the backend will ignore it and use the email from the token.
- **Alternative (frontend):** If the JWT will not have email:
  1. Get the current user’s **customer** (e.g. from an endpoint that returns the customer linked to the logged-in user).
  2. Send that customer’s **valid `customerId`** (GUID) in the `POST /api/bookings/lalamove` body so the handler finds an existing customer and does not return "Customer not found".

---

## Step 3 (Optional): Get Available Drivers

**Endpoint:** `GET /api/bookings/{bookingId}/available-drivers`  
**Auth:** Required (customer).

### Response (200 OK)

```json
{
  "success": true,
  "data": [
    {
      "driverId": "guid",
      "driverName": "...",
      "distanceKm": 5.2,
      "rating": 4.8,
      "isFavouriteDriver": false,
      "estimatedArrivalMinutes": 12,
      "profilePictureUrl": null
    }
  ]
}
```

Note: May return an empty array if driver matching is not fully wired.

---

## Step 4 (Optional): Select Driver

**Endpoint:** `POST /api/bookings/{bookingId}/select-driver`  
**Content-Type:** `application/json`  
**Auth:** Required (customer; must own the booking).

### Request Body

```json
{
  "driverId": "guid-of-driver"
}
```

### Response (200 OK)

```json
{
  "success": true,
  "data": { /* updated BookingDto */ }
}
```

---

## Vehicle Types

Vehicle types are **not** a fixed enum in the API. They come from the **VehiclePricing** table (configurable by admin). The frontend should get the list of available vehicle types from the API.

**Endpoint:** `GET /api/vehicle-pricing`  
**Auth:** None (`AllowAnonymous`).

**Response (200):** List of active vehicle pricings. Each item has:
- `vehicleType` – string to use in `calculate-fare` and `lalamove` (e.g. `"Van"`, `"L300"`).
- `types` – display label (e.g. `"7-seater SUV / Small Van"`).
- `baseFare`, `perKm0to5`, `perKmAbove5`, `additionalStopFee`, `weightLimitKg`, `sizeLimit`, etc.

**Flow:** Call `GET /api/vehicle-pricing` once (e.g. on app load or booking screen). Use each `vehicleType` value in the dropdown; send that same string as `vehicleType` in `POST /api/bookings/calculate-fare` and `POST /api/bookings/lalamove`.

**Seeded defaults (examples):** After initial seed, the API typically has these vehicle types. The actual list depends on your database and admin changes.

| vehicleType | types (display) |
|-------------|------------------|
| Sedan | Hatchback/Sedan |
| SUV | Subcompact SUV / Crossover |
| Van | 7-seater SUV / Small Van |
| Pickup | Pickup |
| L300 | L300 / Cargo Van |
| FB2000 | FB |
| Aluminum2000 | Aluminum |
| Truck3000 | 3,000kg Truck (Aluminum) |
| Truck7000 | 7,000kg Truck |
| Truck12000 | Aluminum / Wing Van |

**Get one by type:** `GET /api/vehicle-pricing/vehicle-type/{vehicleType}` – returns pricing for that vehicle type (e.g. for fare breakdown or validation).

---

## Service Types

| Value | Description |
|-------|--------------|
| Immediate | On-demand delivery |
| Scheduled | Scheduled delivery (e.g. up to 30 days ahead) |

---

## Frontend Flow Summary

1. **User enters:** All stops (1 pickup + 0–19 dropoffs), vehicle type, optional weight/priority.
2. **Call calculate-fare once:** `POST /api/bookings/calculate-fare` with the **full** `stops` array (one request for the whole route, not per stop).
3. **Show:** `data.totalFare` (and optionally `distanceKm`, `breakdown`) to the user.
4. **User confirms** → **Call:** `POST /api/bookings/lalamove` with the **same stops** and `estimatedFare: data.totalFare`, plus `cargoDescription`, `scheduleDate`, `serviceType`.
5. **Optional:** Call `GET /api/bookings/{id}/available-drivers`, then `POST /api/bookings/{id}/select-driver` with chosen `driverId`.

---

## Example: Fetch (Step 1 + Step 2)

```javascript
const API_BASE = 'https://your-api.com';
const token = 'your-jwt-token';
const headers = {
  'Content-Type': 'application/json',
  Authorization: `Bearer ${token}`,
};

const stops = [
  { sequence: 0, address: '123 Pickup St', type: 'Pickup', latitude: 14.5, longitude: 121.0 },
  { sequence: 1, address: '456 Dropoff St', type: 'Dropoff', latitude: 14.6, longitude: 121.1 },
];

// Step 1: Calculate fare
const fareRes = await fetch(`${API_BASE}/api/bookings/calculate-fare`, {
  method: 'POST',
  headers,
  body: JSON.stringify({ vehicleType: 'Van', stops, weightKg: 10 }),
});
const fareJson = await fareRes.json();
if (!fareJson.success) throw new Error(fareJson.message);
const totalFare = fareJson.data.totalFare;

// Step 2: Create booking
const createRes = await fetch(`${API_BASE}/api/bookings/lalamove`, {
  method: 'POST',
  headers,
  body: JSON.stringify({
    vehicleType: 'Van',
    cargoDescription: 'Documents',
    scheduleDate: new Date().toISOString(),
    serviceType: 'Immediate',
    stops,
    estimatedFare: totalFare,
  }),
});
const createJson = await createRes.json();
if (!createJson.success) throw new Error(createJson.message);
const booking = createJson.data;
console.log('Created booking:', booking.id, booking.bookingNumber);
```

---

## API Completeness

This document is **fully aligned with the backend** for the Lalamove-style flow:

- **Calculate fare:** `CalculateFareQuery` → `PricingResultDto` (all fields and validation rules match).
- **Create booking:** `CreateBookingLalamoveDto` → `CreateBeeLogisticsBookingCommand` (stops 1–20, exactly one Pickup, `ServiceType` Immediate/Scheduled, optional fields). Backend resolves customer from JWT when `customerId` is empty GUID.
- **Available drivers:** `GetAvailableDriversQuery` → list of `AvailableDriverDto` (may be empty if driver matching not wired).
- **Select driver:** `SelectDriverRequest` (driverId) → updated `BookingDto`.

Validation (at least one stop, max 20, exactly one Pickup, valid `serviceType`) and error messages match the handlers. The create response is the legacy `BookingDto` (no `stops` in response).

---

## Related Docs

- **Full Bookings API:** `docs/BOOKINGS_API_FRONTEND_GUIDE.md`
- **Registration / Auth:** `docs/REGISTRATION_API_FRONTEND_GUIDE.md`
