# Driver Offers Pending – Why IDs Can Change Between Calls

## Endpoint

- **GET /api/driver-offers/pending** – returns pending offers for the authenticated driver.
- **POST /api/driver-offers/{id}/accept** – accept an offer (use **offer id** in `{id}`).
- **POST /api/driver-offers/{id}/reject** – reject an offer (use **offer id** in `{id}`).

## Response shape (per item)

Each item in the pending list is a **DriverBookingOfferWithDetailsDto** with (among others):

| JSON property   | Type   | Meaning |
|-----------------|--------|--------|
| **id**          | Guid   | **Offer ID** – use this for accept/reject URLs. |
| **bookingId**   | Guid   | Booking this offer refers to (do not use for accept/reject). |
| driverId, status, offeredAt, expiresAt, ... | | |
| bookingNumber, customerName, stops, ... | | Booking details. |

**Important:** Accept and Reject expect the **offer id** (`id`), not `bookingId`. Using `bookingId` in the URL will cause "Offer not found" or wrong offer.

## Why the list (and IDs) can change between calls

The backend does **not** persist or return a stable “session” list. Each GET recalculates the list:

1. **Repository** returns pending offers for the driver where:
   - `Status == Pending`
   - `ExpiresAt > now` (non-expired only),
   - ordered by `OfferedAt` (oldest first),
   - limited by `limit` (default 3, max 10).

2. **Handler** then **skips** an offer if:
   - the booking is already assigned (`SelectedDriverId` set),
   - the booking is Cancelled or Completed,
   - or the booking is not found.

So between two GET calls:

- Some offers may **expire** (no longer in the list).
- **New** offers may appear (new bookings offered to the driver).
- Some offers may **disappear** because the booking was assigned to another driver or cancelled.

So the **set of items** (and their `id`s) can change on every poll. The “first” item might be a different offer (different `id`) next time; that’s expected.

## Code path (for debugging)

- **Controller:** `DriverOffersController.cs` – GET pending, POST accept/reject.
- **Query:** `GetPendingOffersQuery(DriverId, Limit)`.
- **Handler:** `GetPendingOffersQueryHandler` in `DriverOfferHandlers.cs`:
  - Calls `_offerRepository.GetPendingOffersForDriverAsync(driverId, limit)`.
  - For each offer, loads booking via `_bookingRepository.GetByIdAsync(offer.BookingId)`.
  - Skips offer if booking is assigned/cancelled/completed.
  - Builds `DriverBookingOfferWithDetailsDto(offer.Id, offer.BookingId, ...)` – **Id is always the offer’s primary key.**
- **Repository:** `DriverBookingOfferRepository.GetPendingOffersForDriverAsync` – filter by driver, Pending, non-expired, order by OfferedAt, take limit.

There is no code path that overwrites or swaps `id` with `bookingId`; the “changing” IDs are from the list content changing (different offers) between requests.

## Client recommendations

1. Use the **offer `id`** from each item for **POST .../accept** and **POST .../reject**.
2. Do not use `bookingId` for accept/reject.
3. Treat the pending list as a fresh snapshot each time; avoid assuming the same index or same `id` on the next poll.
4. If an accept/reject returns 404, the offer may have expired or already been accepted/rejected; refresh the pending list.
