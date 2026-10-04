# Bookings API – Frontend Guide

This document is **scanned from the actual backend code** (BookingsController, BookingHandlers, LalamoveBookingHandlers, DriverOffersController, DTOs) and describes all booking-related endpoints, request/response shapes, and how to implement them in the frontend.

**Base URL:** `https://your-api-domain.com`  
**Auth:** All endpoints require **JWT** in `Authorization: Bearer <token>` unless noted.

---

## Table of Contents

1. [Standard API response format](#standard-api-response-format)
2. [Pagination note (bookings history)](#pagination-note-bookings-history)
3. [Endpoints reference](#endpoints-reference)
4. [Booking data shapes](#booking-data-shapes)
5. [List & get bookings](#list--get-bookings)
6. [Create booking (legacy vs Lalamove)](#create-booking-legacy-vs-lalamove)
7. [Update status & delete](#update-status--delete)
8. [Calculate fare & Lalamove flow](#calculate-fare--lalamove-flow)
9. [Driver offers](#driver-offers)
10. [Backoffice / admin endpoints](#backoffice--admin-endpoints)
11. [Enums](#enums)
12. [Frontend examples](#frontend-examples)

---

## Standard API response format

All successful responses use this wrapper:

```json
{
  "success": true,
  "message": null,
  "data": { ... },
  "errors": []
}
```

Errors (400 / 404):

```json
{
  "success": false,
  "message": "Booking not found",
  "data": null,
  "errors": []
}
```

Validation errors may include `errors` array of strings.

---

## Pagination note (bookings history)

- **`GET /api/bookings`** (admin/fleet list) **supports server-side pagination** via query params `page` and `pageSize`. Response is a `PagedResult` (see [GET /api/bookings](#get-apibookings) below).
- **`GET /api/bookings/my-bookings`** and **`GET /api/bookings/customer/{customerId}`** return the **full list** (no pagination). For large lists, use **client-side pagination** in the UI (e.g. fetch once, show 10–20 per page).

---

## Endpoints reference

| Method | Endpoint | Auth | Purpose |
|--------|----------|------|--------|
| GET | `/api/bookings` | SuperAdmin, Admin, Owner, Dispatcher | List all (or by tenant) |
| GET | `/api/bookings/{id}` | Any authenticated | Get one booking |
| GET | `/api/bookings/customer/{customerId}` | Any authenticated | List by customer |
| GET | `/api/bookings/my-bookings` | Any authenticated | **Current user’s booking history** |
| POST | `/api/bookings` | Any authenticated | Create (legacy form) |
| POST | `/api/bookings/calculate-fare` | Any authenticated | Get fare before creating |
| POST | `/api/bookings/lalamove` | Any authenticated | Create (Lalamove-style, multi-stop) |
| PATCH | `/api/bookings/{id}/status` | Any authenticated | Update status |
| DELETE | `/api/bookings/{id}` | Any authenticated | Delete booking |
| GET | `/api/bookings/{id}/available-drivers` | Any authenticated | Drivers for booking |
| POST | `/api/bookings/{id}/select-driver` | Any authenticated | Select driver |
| POST | `/api/bookings/{id}/stops/{stopId}/complete` | Driver | Mark stop complete |
| POST | `/api/bookings/{id}/stops/{stopId}/pod` | Driver | Upload proof of delivery |
| GET | `/api/bookings/{id}/item-image` | Any (with access) | Get item image |
| GET | `/api/bookings/pending-assignment` | SuperAdmin, Admin | Pending assignments |
| GET | `/api/bookings/assigned-to-me` | Any authenticated | Bookings assigned to my tenant |
| POST | `/api/bookings/{id}/classify-size` | SuperAdmin, Admin | Classify size |
| POST | `/api/bookings/{id}/assign-to-operator` | SuperAdmin, Admin | Assign to operator |
| POST | `/api/bookings/{id}/start-broadcast` | SuperAdmin, Admin | Start broadcast |
| GET | `/api/driver-offers/pending` | Driver | My pending offers |
| POST | `/api/driver-offers/{id}/accept` | Driver | Accept offer |
| POST | `/api/driver-offers/{id}/reject` | Driver | Reject offer |

---

## Booking data shapes

### BookingDto (single booking in list/detail)

```ts
{
  id: string;           // GUID
  bookingNumber: string;
  customerId: string;
  customerName: string;
  pickupLocation: string;
  dropoffLocation: string;
  truckType: string;
  cargoDescription: string;
  scheduleDate: string;  // ISO date-time
  status: string;       // see BookingStatus enum
  notes: string | null;
  createdAt: string;
  updatedAt: string | null;
  size: string | null;   // Small | Large | Heavy (legacy)
  assignmentStatus: string;
  assignedToTenantId: string | null;
  assignedByUserId: string | null;
  assignedAt: string | null;
  beeTenantId: string | null;
  weightKg: number | null;
  pickupLatitude: number | null;
  pickupLongitude: number | null;
  dropoffLatitude: number | null;
  dropoffLongitude: number | null;
  itemImagePath: string | null;
}
```

---

## List & get bookings

### GET `/api/bookings` (paginated list)

**Auth:** `SuperAdmin`, `Admin`, `Owner`, `Dispatcher`.

- **SuperAdmin/Admin:** Returns all bookings (paginated).
- **Owner/Dispatcher:** Returns only bookings for their tenant (filtered by `AssignedToTenantId` / company), paginated.

**Query parameters:**

| Parameter  | Type   | Default | Description |
|------------|--------|---------|-------------|
| `page`     | number | `1`     | Page number (1-based). |
| `pageSize` | number | `50`    | Items per page. Backend clamps to 1–500 (values &lt; 1 become 10, &gt; 500 become 500). |

**Example request:**

```
GET /api/bookings?page=2&pageSize=20
Authorization: Bearer <token>
```

**Response (200):**

`data` is a **paged result object** (not a raw array):

```json
{
  "success": true,
  "message": null,
  "data": {
    "items": [
      { /* BookingDto */ },
      ...
    ],
    "totalCount": 156,
    "page": 2,
    "pageSize": 20,
    "totalPages": 8,
    "hasNextPage": true,
    "hasPreviousPage": true
  },
  "errors": []
}
```

**TypeScript shape:**

```ts
interface PagedBookingsResponse {
  success: boolean;
  data: {
    items: BookingDto[];
    totalCount: number;
    page: number;
    pageSize: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}
```

**Frontend usage:** Use `data.items` for the current page, `data.totalCount` for total count, and `data.page` / `data.pageSize` / `data.totalPages` / `data.hasNextPage` / `data.hasPreviousPage` for building pagination UI.

---

### GET `/api/bookings/my-bookings`

**Auth:** Any authenticated user.

Returns bookings for the **current user** (resolved by JWT → user email → customer → bookings). Use this for **“My booking history”** in the customer app.

**Query params:** None.

**Response (200):**

```json
{
  "success": true,
  "data": [
    { /* BookingDto */ },
    ...
  ]
}
```

If the user has no customer record, `data` is an empty array.

---

### GET `/api/bookings/customer/{customerId}`

**Auth:** Any authenticated.

Returns all bookings for the given `customerId` (GUID). No pagination.

**Response (200):** Same as above – `data` is array of `BookingDto`.

---

### GET `/api/bookings/{id}`

**Auth:** Any authenticated (customer/driver/admin – handler may enforce access).

**Response (200):** `{ "success": true, "data": { /* BookingDto */ } }`  
**404:** Booking not found.

---

## Create booking (legacy vs Lalamove)

### Option A: Legacy – POST `/api/bookings` (multipart/form-data)

**Content-Type:** `multipart/form-data`.

**Form fields:**

| Field | Type | Required | Notes |
|-------|------|----------|--------|
| customerId | GUID | No | Omitted = use current user as customer |
| pickupLocation | string | Yes | Max 500 |
| dropoffLocation | string | Yes | Max 500 |
| truckType | string | Yes | Max 50 |
| cargoDescription | string | No | Max 1000 |
| scheduleDate | datetime | Yes | Must be today or future |
| notes | string | No | Max 1000 |
| weightKg | number | No | 0–100000 |
| pickupLatitude | number | No | -90–90 (pair with longitude) |
| pickupLongitude | number | No | -180–180 |
| dropoffLatitude | number | No | -90–90 |
| dropoffLongitude | number | No | -180–180 |
| itemImage | file | No | JPG/PNG, max ~10 MB |

**Validation:** Pickup/dropoff lat/long must be provided in pairs. Schedule date must be today or future. Image: JPG/PNG only, virus scan may apply.

**Response (200):** `{ "success": true, "data": { /* BookingDto */ } }`  
**201:** Created (if using CreatedAtRoute).  
**400:** Validation failed, duplicate booking, or file error.

---

### Option B: Lalamove-style – POST `/api/bookings/lalamove` (JSON)

**Content-Type:** `application/json`.

**Request body:**

```ts
{
  customerId: string;       // GUID; can be omitted if using current user
  vehicleType: string;
  cargoDescription: string;
  scheduleDate: string;    // ISO date-time
  serviceType: string;     // "Immediate" | "Scheduled"
  stops: Array<{
    sequence: number;
    address: string;
    type: string;           // "Pickup" | "Dropoff"
    latitude?: number;
    longitude?: number;
    contactName?: string;
    contactPhone?: string;
    notes?: string;
  }>;
  estimatedFare: number;
  weightKg?: number;
  priorityFee?: number;
  scheduledDateTime?: string;
  scheduledPickupWindow?: string;
  favouriteDriverId?: string;
  itemImagePath?: string;
  notes?: string;
}
```

**Rules:** At least one stop; max 20 stops; exactly one stop with `type: "Pickup"`.

**Response (200):** `{ "success": true, "data": { /* BookingDto */ } }`  
**400:** Validation (e.g. “Exactly one pickup stop is required”, “Maximum 20 stops allowed”).

---

## Update status & delete

### PATCH `/api/bookings/{id}/status`

**Auth:** Any authenticated (ownership validated in handler).

**Request body:**

```json
{
  "status": "Completed"
}
```

**Allowed status values:** See [Enums](#enums) – e.g. `Pending`, `Confirmed`, `DriverAssigned`, `PickedUp`, `InTransit`, `Completed`, `Cancelled`. Legacy numeric/names may also work where still supported.

**Response (200):** `{ "success": true, "data": { /* BookingDto */ } }`  
**400/404:** Not found or invalid transition.

---

### DELETE `/api/bookings/{id}`

**Auth:** Any authenticated (ownership validated).

**Response (200):** `{ "success": true }` (no `data`).  
**404:** Booking not found.

---

## Calculate fare & Lalamove flow

### POST `/api/bookings/calculate-fare`

Get estimated fare **before** creating a booking (same stop structure as Lalamove create).

**Request body:**

```json
{
  "vehicleType": "Van",
  "stops": [
    { "sequence": 0, "address": "123 Pickup St", "type": "Pickup", "latitude": 14.5, "longitude": 121.0 },
    { "sequence": 1, "address": "456 Dropoff St", "type": "Dropoff", "latitude": 14.6, "longitude": 121.1 }
  ],
  "weightKg": 10,
  "priorityFee": 50,
  "scheduledDateTime": "2026-02-01T10:00:00Z"
}
```

**Response (200):**

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

Use `totalFare` (and optionally `estimatedFare` from this) when calling POST `/api/bookings/lalamove`.

**Suggested flow:**

1. User enters stops and options → `POST /api/bookings/calculate-fare`.
2. Show `data.totalFare` (and breakdown) to user.
3. User confirms → `POST /api/bookings/lalamove` with same stops and `estimatedFare: data.totalFare`.

---

### GET `/api/bookings/{id}/available-drivers`

**Auth:** Any authenticated.

Returns list of available drivers for a **Pending** booking. Currently may return an empty list if driver matching is not fully wired.

**Response (200):**

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

---

### POST `/api/bookings/{id}/select-driver`

**Auth:** Any authenticated (customer). Handler checks `booking.CustomerId === current user`.

**Request body:**

```json
{
  "driverId": "guid-of-driver"
}
```

**Response (200):** `{ "success": true, "data": { /* BookingDto */ } }`  
**400:** Booking not pending or unauthorized.

---

### POST `/api/bookings/{id}/stops/{stopId}/complete`  
### POST `/api/bookings/{id}/stops/{stopId}/pod`

**Auth:** Driver only.

- **complete:** Mark stop as completed (body not required in current impl).
- **pod:** Upload proof of delivery – request shape from controller: `{ imagePath, signaturePath?, recipientName?, notes? }` (form or JSON as implemented).

Use for driver app “complete stop” and “upload POD” flows.

---

## Driver offers

**Base path:** `/api/driver-offers`. All require **Driver** JWT (user id = driver id).

### GET `/api/driver-offers/pending`

Returns pending offers for the current driver.

**Response (200):** `{ "success": true, "data": [ /* DriverBookingOfferDto */ ] }`

### POST `/api/driver-offers/{id}/accept`

**Response (200):** Success with updated booking/offer data.

### POST `/api/driver-offers/{id}/reject`

**Response (200):** Success.

---

## Backoffice / admin endpoints

- **GET `/api/bookings/pending-assignment`** – SuperAdmin, Admin: bookings pending assignment.
- **GET `/api/bookings/assigned-to-me`** – Bookings assigned to current user’s tenant.
- **POST `/api/bookings/{id}/classify-size`** – Body: `{ "size": "Small" }` (Small | Large | Heavy).
- **POST `/api/bookings/{id}/assign-to-operator`** – Body: `{ "operatorTenantId": "guid" }`.
- **POST `/api/bookings/{id}/start-broadcast`** – Start broadcasting to drivers (no body).

All return standard `success`/`data`/`message` wrapper.

---

## Enums

### BookingStatus (string)

- `Pending` – Created, waiting for driver.
- `Confirmed` – Driver accepted.
- `DriverAssigned` – Driver en route to pickup.
- `PickedUp` – Driver picked up.
- `InTransit` – Delivering.
- `Completed` – All stops done.
- `Cancelled`

Legacy aliases (e.g. `Dispatched`, `OnTheWayToPickup`, `InProgress`, `Delivered`) may still be returned or accepted where supported.

### BookingSize (legacy)

- `Small`, `Large`, `Heavy`

### ServiceType (Lalamove)

- `Immediate`, `Scheduled`

### Stop type (in stops array)

- `Pickup`, `Dropoff`

---

## Frontend examples

### Get paginated bookings (admin/fleet list)

```ts
const getBookingsPage = async (page = 1, pageSize = 20) => {
  const res = await fetch(
    `${API_BASE}/api/bookings?page=${page}&pageSize=${pageSize}`,
    { headers: { Authorization: `Bearer ${token}` } }
  );
  const json = await res.json();
  if (!json.success) throw new Error(json.message ?? 'Failed to load bookings');
  return json.data; // { items, totalCount, page, pageSize, totalPages, hasNextPage, hasPreviousPage }
};

// Usage
const paged = await getBookingsPage(2, 20);
console.log(paged.items);        // BookingDto[] for current page
console.log(paged.totalCount);   // total number of bookings
console.log(paged.totalPages);   // total pages
if (paged.hasNextPage) { /* show next button */ }
```

### Get my booking history (then paginate in UI)

```ts
const getMyBookings = async (): Promise<BookingDto[]> => {
  const res = await fetch(`${API_BASE}/api/bookings/my-bookings`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const json = await res.json();
  if (!json.success) throw new Error(json.message ?? 'Failed to load bookings');
  return json.data ?? [];
};

// Client-side pagination
const pageSize = 10;
const allBookings = await getMyBookings();
const page = 2;
const start = (page - 1) * pageSize;
const pageItems = allBookings.slice(start, start + pageSize);
const totalPages = Math.ceil(allBookings.length / pageSize);
```

### Calculate fare then create Lalamove booking

```ts
const stops = [
  { sequence: 0, address: '123 Pickup St', type: 'Pickup', latitude: 14.5, longitude: 121.0 },
  { sequence: 1, address: '456 Dropoff St', type: 'Dropoff', latitude: 14.6, longitude: 121.1 },
];

const fareRes = await fetch(`${API_BASE}/api/bookings/calculate-fare`, {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
  },
  body: JSON.stringify({
    vehicleType: 'Van',
    stops,
    weightKg: 10,
  }),
});
const fareJson = await fareRes.json();
if (!fareJson.success) throw new Error(fareJson.message);
const estimatedFare = fareJson.data.totalFare;

const createRes = await fetch(`${API_BASE}/api/bookings/lalamove`, {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
  },
  body: JSON.stringify({
    vehicleType: 'Van',
    cargoDescription: 'Documents',
    scheduleDate: new Date().toISOString(),
    serviceType: 'Immediate',
    stops,
    estimatedFare,
  }),
});
const createJson = await createRes.json();
if (!createJson.success) throw new Error(createJson.message);
const booking = createJson.data;
```

### Legacy create (multipart)

```ts
const form = new FormData();
form.append('pickupLocation', '123 Pickup St');
form.append('dropoffLocation', '456 Dropoff St');
form.append('truckType', 'Van');
form.append('cargoDescription', 'Cargo');
form.append('scheduleDate', new Date().toISOString());
if (file) form.append('itemImage', file);

const res = await fetch(`${API_BASE}/api/bookings`, {
  method: 'POST',
  headers: { Authorization: `Bearer ${token}` },
  body: form,
});
const json = await res.json();
if (!json.success) throw new Error(json.message);
return json.data;
```

### Update booking status

```ts
await fetch(`${API_BASE}/api/bookings/${bookingId}/status`, {
  method: 'PATCH',
  headers: {
    'Content-Type': 'application/json',
    Authorization: `Bearer ${token}`,
  },
  body: JSON.stringify({ status: 'Cancelled' }),
});
```

---

## Summary

| Use case | Endpoint | Pagination |
|----------|----------|------------|
| All bookings (admin/fleet) | `GET /api/bookings` | **Yes** – `page`, `pageSize`; response is `PagedResult` |
| My booking history | `GET /api/bookings/my-bookings` | No – use **client-side** pagination on full list |
| Single booking | `GET /api/bookings/{id}` | N/A |
| Create (simple) | `POST /api/bookings` (form) | N/A |
| Create (multi-stop) | `POST /api/bookings/lalamove` (JSON) | N/A |
| Fare before create | `POST /api/bookings/calculate-fare` | N/A |
| Update status | `PATCH /api/bookings/{id}/status` | N/A |
| Delete | `DELETE /api/bookings/{id}` | N/A |

- **`GET /api/bookings`** returns `data` as a **PagedResult** (`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`).
- **Other list endpoints** (`my-bookings`, `customer/{id}`, etc.) return `data` as an array of `BookingDto`.
- Single-resource endpoints return `data` as one `BookingDto` or the relevant DTO. Use the standard `success`/`message`/`data`/`errors` wrapper for every response.
