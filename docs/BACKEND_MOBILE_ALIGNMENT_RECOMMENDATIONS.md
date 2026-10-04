# Backend ↔ Mobile Alignment & Flow Recommendations

> **Scope:** Read-only scan of `bee-backend`, `bee-customer`, and `bee-driver`.
> **Date:** 2026-06-11
> **Nothing in the codebase was modified.** This is an audit + recommendations document only.

This covers four areas you asked about: the **booking process**, the **payment process**, **retries**, and **broadcasting to drivers** (including a proposed alternative broadcasting algorithm). Each section states what exists today, how well backend and the two mobile apps line up, and concrete recommendations ranked by impact.

---

## 0. System at a glance

**Backend** — modular .NET monolith (`BeeLogistics.Api` + per-domain modules under `src/Modules`). Key infra:
- **MassTransit + RabbitMQ** for async messaging, with the **EF Core Outbox** on `BookingsDbContext` for transactional publishing.
- **Hangfire** (PostgreSQL storage) for recurring jobs: offer expiry, driver-queue processing, broadcast pulse, outbox dispatch.
- **Redis H3 geo-index** for nearest-driver lookups, with a full DB scan fallback.
- **Xendit** for cashless payments (invoices + async refunds), webhooks into `WebhooksController`.
- **SignalR hubs** (`/hubs/notifications`, `/hubs/location`, `/hubs/chat`) + **SSE** + **Expo push** (`CombinedNotificationService`) for realtime/notifications.
- **MQTT** (`mqtt.ilocosscript.live`) for driver location ingestion.

**Customer app (`bee-customer`)** — Expo/React Native. Creates bookings via `POST /api/bookings/lalamove`, pays via Xendit invoice (WebBrowser), tracks payment via **SSE + 3s polling fallback**, subscribes to `BookingStatusChanged` over SignalR notifications hub for live tracking.

**Driver app (`bee-driver`)** — Expo/React Native. Pulls offers via **5s polling** of `GET /api/driver-offers/pending`, accepts/rejects, publishes location over MQTT, runs status transitions through `POST /api/bookings/{id}/stops/...`.

---

## 1. Booking process

### How it works today
- Customer app builds a multi-stop payload and calls `POST /api/bookings/lalamove` (`createMultiStopBooking`). A legacy `POST /api/bookings` (single pickup/dropoff) path also still exists in the backend (`CreateBookingCommandHandler`).
- `CreateBeeLogisticsBookingCommandHandler` validates stops (exactly one pickup, ≤20 stops), gets-or-creates the `Customer` from the JWT email, creates the `Booking` (status `Pending`), and **publishes `BookingBroadcastRequested` before `SaveChanges`** so the outbox row commits in the same transaction. For Cash it also creates a cash-on-delivery `Payment`.
- The domain `Booking` has a clean Lalamove-style status machine: `Pending → Confirmed → DriverAssigned → PickedUp → InTransit → Completed` (+ `Cancelled`), plus a large set of `[Obsolete]` legacy statuses/fields kept for backward compatibility.

### Alignment findings
| Area | Status | Notes |
|------|--------|-------|
| Create booking (multi-stop) | ✅ Aligned | Customer `lalamove` payload matches `CreateBookingLalamoveDto`. |
| Status enum | ⚠️ Partial drift | Customer app `validStatuses` lists `Assigned`, `Broadcasting`, `Dispatched`, `InProgress`, `Arrived` — several of these are backend **legacy aliases** or don't map 1:1 to the new `BookingStatus`. Works due to case-insensitive matching + `Pending` fallback, but it's fragile. |
| `GET /api/bookings/stats` | ❌ Likely broken | Customer `getBookingStats()` calls `/api/bookings/stats`, but the Bookings controller has **no `stats` route** (only `DashboardController` exposes `stats`). This call will 404 silently. |
| `POST /api/bookings/upload-item-image` | ❌ Route mismatch | Customer `uploadBookingItemImage()` posts to `/api/bookings/upload-item-image`, but backend only has `POST /api/bookings/{id}/item-image` (requires an existing booking id). The standalone helper would 404 — it appears superseded by multipart-on-create, but the dead helper is a trap. |
| Cancellation | ✅ Aligned | Both apps call `POST /api/bookings/{id}/cancel` with `{reason, customReason}`; backend resolves enum→text and authorizes customer/driver. |
| Duplicate suppression | ✅ Good | Legacy create path has a 5-minute idempotency window (`FindDuplicateBookingAsync`). |

### Recommendations
1. **Define one canonical `BookingStatus` contract** and share it. Backend exposes ~6 real statuses but maps ~10 legacy aliases onto the same integer values (`Dispatched = DriverAssigned`, `Delivered = Completed`, etc.). The mobile apps each re-declare their own slightly different string lists. Publish a single source of truth (an OpenAPI enum, or a generated TS type) and delete the aliases the apps don't actually need. **Impact: prevents silent status-mapping bugs as the enum evolves.**
2. **Fix or remove the `/api/bookings/stats` and `/api/bookings/upload-item-image` client calls.** Either add the routes or delete the dead client methods. A 404 swallowed by a try/catch is how "the stats screen is just always empty" bugs hide.
3. **The `CreateBeeLogisticsBookingCommandHandler` does two separate `SaveChangesAsync` calls** (booking+outbox, then cash payment) across two DbContexts with no shared transaction. If the cash-payment save fails, the booking + broadcast already committed → a broadcast goes out for a booking with no payment record. Consider creating the cash payment in the same unit of work, or making the `EarningCredit`/`CashDeliverySettlement` consumers tolerant of a missing payment (create-on-demand).
4. **`GetAvailableDriversQueryHandler` is a stub** — it returns an empty list with `// TODO: Integrate with driver location service`. If the customer "choose your driver" UI calls `GET /api/bookings/{id}/available-drivers`, it always shows nothing. Either wire it to `DriverAvailabilityService` (which already does exactly this) or remove the endpoint so the app doesn't depend on a dead feature.

---

## 2. Payment process

### How it works today
- **Cashless (PayOnline):** customer app creates the payment *first* (`POST /api/payments`, booking id null), gets `xenditInvoiceUrl`, opens it in `WebBrowser`, and listens for `Paid` via `usePaymentStatus` (SSE + 3s poll). On `Paid` it **creates the booking** and calls `link-booking`. Backend `CreatePaymentHandler` creates a Xendit invoice and has an idempotency guard on the Xendit invoice id.
- **Cash:** booking is created immediately with a cash-on-delivery `Payment`; settlement happens on completion.
- **Webhooks:** `WebhooksController` verifies (callback token / RSA signature / IP allowlist), **persists the raw event with a unique event key for idempotency**, then dispatches: invoice → `ProcessWebhookCommand`; `refund.*` → `ProcessRefundWebhookCommand`; `payout.*` → disbursement processor. Returns **500 on failure so Xendit redelivers** (handlers are idempotent). Also publishes `XenditInvoiceWebhookReceived` via `IBus` (bypassing the outbox) for immediate SSE/SignalR update.
- **Refunds:** async by design. `RefundPaymentCommandHandler` marks `RefundPending`, and the `refund.*` webhook finalizes to `Refunded` (publishing `PaymentRefundedEvent` for the driver wallet debit) or back to `Paid` on failure. Refund endpoint is `Admin`/`SuperAdmin` only, time-limited unless bypassed.

### Alignment findings
| Area | Status | Notes |
|------|--------|-------|
| Webhook idempotency + verification | ✅ Strong | Stored event key, status-guarded handlers, 500-for-retry, RSA/token/IP auth, strict-in-production. This is well done. |
| Refund state machine | ✅ Strong | `RefundPending` → webhook finalize; wallet debit only on `SUCCEEDED`. Correct ordering. |
| Payment status to customer | ⚠️ Fragile | SSE streaming "doesn't work well in React Native" (per the app's own comment) so the **3s poll is the real mechanism**. Works, but every paying customer polls `GET /api/payments/{id}` every 3s until paid — wasteful and adds DB load at scale. |
| `GET /api/payments/*` authorization | ⚠️ Review | `PaymentsController` inherits `[Authorize]` but the `GetById` / `GetByBooking` / `GetByCustomer` actions have **no ownership check** (only the SSE `events` endpoint validates `customerId`). Any authenticated user can read any payment by id/booking/customer id. The id is a GUID (hard to guess) but this is still an IDOR exposure. |
| Booking-after-payment race | ⚠️ Edge case | In PayOnline, if the app is killed after `Paid` but before it creates the booking, **the customer has paid but no booking exists**. Recovery depends on the app re-opening and the `paymentStatus` effect re-firing. There's no backend reconciliation that creates/min-flags an orphaned paid payment. |

### Recommendations
1. **Add ownership checks to the payment read endpoints** (`GetById`, `GetByBooking`, `GetByCustomer`) — compare against the JWT's resolved customer, like the SSE endpoint already does. **Impact: closes an IDOR on financial records.** Highest-priority security item in this report.
2. **Reconsider the "pay first, create booking after" ordering.** Creating the booking *before* payment (status `PendingPayment`) and then confirming it on the webhook would (a) remove the "paid but no booking" orphan risk, (b) let the backend own the state transition instead of the client, and (c) let the existing webhook consumer drive it server-side. The client's role shrinks to "open invoice, show result."
3. **Replace the 3s client poll with push.** The webhook already publishes `XenditInvoiceWebhookReceived` and `BookingPaymentWebhookConsumer` already broadcasts a paid event. Deliver that via the **notifications SignalR hub** (which the customer app is already connected to for `BookingStatusChanged`) and/or **Expo push**, and keep polling only as a slow (e.g. 15s) safety net. **Impact: removes N×0.33 req/s of poll load per pending payment.**
4. **Add a Hangfire reconciliation job for stuck payments** — `Paid` payments older than X minutes with no linked booking (PayOnline orphans) and `RefundPending` older than Y hours (Xendit never sent the final webhook). Query Xendit for ground truth and resolve. You already have `DriverTopUpReconciliationService` as a pattern to copy.

---

## 3. Retries

### How it works today
- **MassTransit consumers:** `UseMessageRetry` (immediate: 1s, 5s, 30s) + `UseDelayedRedelivery` (1m, 5m, 15m). Good layered strategy; consumers are described as idempotent (status-guarded). After exhausting both, messages go to `_error`.
- **EF Core Outbox** on `BookingsDbContext` (`QueryDelay` 10s) guarantees `BookingBroadcastRequested` and friends publish only after the DB commit.
- **Hangfire jobs** have `[AutomaticRetry]` with explicit delays and `[DisableConcurrentExecution]`. A separate **Drivers outbox dispatcher** runs every 10s.
- **Webhooks** return 500 on failure → Xendit's own retry redelivers; idempotency makes that safe.
- **Mobile:** customer `getBookings` retries network errors with exponential backoff (1s, 2s, max 2). Driver `useOffers` has explicit **429 rate-limit backoff** (2-min pause, 30s slow-poll), and **stops polling entirely on any non-429 error** until manual refresh.

### Alignment findings
| Area | Status | Notes |
|------|--------|-------|
| Consumer retry/redelivery | ✅ Strong | Sensible immediate + delayed tiers. |
| Outbox | ✅ Strong | Only on `BookingsDbContext`; Payment webhook deliberately uses `IBus` to bypass it for immediacy — documented and reasonable. |
| Dead-letter handling | ⚠️ Gap | Messages that exhaust retries land in `_error` queues with **no alerting or automated drain**. Failures become invisible. |
| Driver offer polling resilience | ⚠️ UX gap | `useOffers` **kills polling on the first non-429 error** ("User must manually refresh"). A single transient 500/timeout means the driver silently stops receiving offers until they notice and pull-to-refresh. |
| No idempotency keys from clients | ⚠️ Gap | Booking/payment creation relies on server-side heuristics (5-min duplicate window) rather than a client-supplied `Idempotency-Key`. Double-submits across the 5-min window or with slightly different payloads can still duplicate. |

### Recommendations
1. **Make driver offer polling self-healing.** On transient errors, keep polling with backoff (like the 429 path) instead of stopping until manual refresh. A driver whose poll silently died is a driver who looks "offline" to the dispatch system but thinks they're available. **Impact: directly affects fill rate.** This is the highest-value retry fix.
2. **Add `_error` queue monitoring + alerting.** Surface dead-lettered message counts in the existing OpenTelemetry/Prometheus metrics and alert on non-zero. Optionally a periodic re-drain for known-transient fault types.
3. **Adopt client-supplied idempotency keys** for `POST /payments` and booking creation (header `Idempotency-Key`, persisted unique). More robust than the time-window heuristic and removes the duplicate-booking class of bugs entirely.
4. **Consider a `DriverOfferResponse` idempotency guard.** Accept/reject already guards on offer status + booking `xmin` concurrency token — good. Just confirm the client doesn't surface a scary error when the "another driver already accepted" race returns `Fail` (it should be a soft "offer taken" UX, not an error toast).

---

## 4. Broadcasting to drivers

### How it works today (current algorithm)

This is the most complex flow. The pipeline:

1. **Trigger:** booking creation publishes `BookingBroadcastRequested` (via outbox). Also re-triggerable by admin (`start-broadcast`).
2. **`BookingBroadcastConsumer`:**
   - First time → `StartBroadcastingToDrivers()`, then find available drivers.
   - Already broadcasting → **"pulse"**: only offer to newly-available drivers not already offered.
3. **`DriverAvailabilityService.GetAvailableDriversAsync`:**
   - If booking has pickup coords → **Redis H3 geo-index** nearest-driver query (over-fetches `maxOffers × 3`), verifies each candidate against Identity (`IsActive`, `Role == "Driver"`), ranks by distance.
   - Else / index empty → **full DB scan** of all active drivers, distance-ranked, optional `ProximityRadiusKm` filter (currently 100 km).
4. **Offer creation:** create up to `MaxOffersPerBroadcast` (default **15**) `DriverBookingOffer` rows, **all with the same 10-min expiry**, unique `(BookingId, DriverId)` index guards against concurrent duplicate offers.
5. **Driver side:** app polls `GET /api/driver-offers/pending?limit=3` every **5s**, accept/reject.
6. **Accept:** `AcceptBookingOfferCommandHandler` guards on offer status + booking `xmin` optimistic concurrency (handles simultaneous accepts), cancels all other pending offers, notifies the customer.
7. **Hangfire jobs:**
   - `pulse-broadcasting-bookings` (every 10s) — re-offers to newly available drivers; also rescues `PendingAssignment` bookings (<60 min old) whose initial outbox message was never consumed.
   - `process-expired-offers` (every 30s) — expires offers; if a booking has no pending offers left, marks `RejectedByAllDrivers`.
   - `process-driver-queue` (every 30s) — moves to next driver when current offer expires (30s grace).

### Alignment findings
| Area | Status | Notes |
|------|--------|-------|
| Concurrency safety | ✅ Strong | `(BookingId, DriverId)` unique index + booking `xmin` token correctly handle the double-accept race and concurrent offer-batch creation. |
| Geo-matching + fallback | ✅ Good | H3 index with graceful DB-scan fallback is a solid design. |
| **Drivers are not actively notified of new offers** | ❌ **Major gap** | Offer creation writes DB rows but **publishes no push/SignalR event to the driver**. Drivers only discover offers via 5s polling. The `CombinedNotificationService` (SignalR + Expo push) exists and is used for other events, but **nothing calls it when an offer is created**. The driver app has no SignalR subscription for offers at all (only chat + wallet). |
| "Broadcast to closest 15, all at once" | ⚠️ Design smell | Every broadcast offers the same job to up to 15 drivers simultaneously with identical 10-min expiry. Whoever's app polls and taps first wins. This favors fast pollers / fast phones over the best driver, and 15 drivers all see a job that 14 will lose ("offer thrash"). |
| Polling cost | ⚠️ Scale | Every online driver polls every 5s regardless of whether offers exist. 1,000 online drivers = 200 req/s of mostly-empty offer polls. |
| Mixed old/new model | ⚠️ Tech debt | `StartBroadcastingToDrivers`, `AssignmentStatus`, `MarkAsRejectedByAllDrivers` are all `[Obsolete]` but are **the live mechanism**. The "deprecated" labels are misleading — the broadcast system depends on them. |
| Config not fully wired | ⚠️ Minor | `appsettings` has `OfferExpirationManyDriversMinutes`/`OfferExpirationFewDriversMinutes` (2 / 5) but the consumers hardcode a flat **10-minute** expiry — the adaptive-expiry config is dead. |

### Recommendations — incremental (keep current architecture)
1. **Push offers to drivers when they're created.** This is the single highest-impact change. In `CreateOffersForDriversAsync`, after the offers save, call `CombinedNotificationService.SendToUserAsync(driverId, ...)` (SignalR + Expo push) with a "New booking offer" payload. Add a SignalR `NewOffer` subscription in the driver app that refetches/prepends. **Impact: cuts time-to-accept from up-to-5s-poll-latency to near-instant, works when the app is backgrounded (push), and lets you drop the poll interval.** Everything you need (`CombinedNotificationService`, device tokens, hub) already exists.
2. **Then lengthen the poll to a slow safety net** (e.g. 20–30s) once push is the primary path. Removes the bulk of the empty-poll load.
3. **Wire up the adaptive expiry config** (`OfferExpiration{Many,Few}DriversMinutes`) that's already in `appsettings` but ignored: short expiry when many drivers are available (fast hand-off), longer when few.
4. **Rename/retire the misleading `[Obsolete]` labels** on the broadcast methods, or genuinely migrate off them. Right now a new developer will "clean up" deprecated code and break dispatch.

### Recommendation — alternative broadcasting algorithm (design proposal)

The current model is **"blast to nearest N, first-tap-wins."** Consider moving to a **tiered / sequential-with-overlap offer model**, which is what Lalamove/Uber/Grab converge on:

**Proposed: Scored sequential waves with short expiry**

1. **Score, don't just sort by distance.** Rank candidate drivers by a weighted score:
   ```
   score = w1·proximity + w2·driverRating + w3·acceptanceRate
         + w4·idleTime (fairness) + w5·favouriteDriverBonus
         + w6·directionMatch (driver already heading that way)
   ```
   You already capture distance, rating, and favourite-driver flags on the offer — this extends naturally.
2. **Offer in small waves, not one big blast.** Wave 1 = top 1–3 drivers, **short expiry (20–30s)**. If none accept, expire and immediately send wave 2 = next 1–3, and so on. This:
   - Gives the *best* driver first refusal instead of the *fastest poller*.
   - Eliminates "offer thrash" (14 of 15 always lose).
   - Naturally feeds your existing `process-driver-queue` job, which is already built for "move to next."
3. **Overlap waves slightly** (e.g. start wave 2 at 80% of wave 1's expiry) so there's no dead air, trading a little redundancy for latency.
4. **Backstop with a widening radius.** If waves exhaust within `ProximityRadiusKm`, widen the radius and/or fall back to the current broad blast so a job is never stuck silently.
5. **Driver-side acceptance SLA & fairness.** Track per-driver acceptance rate and idle time; the `idleTime` term keeps it fair (a driver who's been waiting longest gets weighted up), which improves driver retention.

**Why this fits your codebase well:** the offer table, expiry processing, next-in-queue job, distance/rating/favourite fields, and concurrency guards already exist. The change is mostly in *how many* offers you create per wave and *the ordering/score*, plus driving it from the queue job rather than one upfront batch. It's an evolution, not a rewrite.

**Trade-off:** sequential waves increase time-to-assignment in low-density areas (fewer drivers, more waves). Mitigate with the overlap (#3) and the widening-radius backstop (#4), and tune wave size up when supply is thin. Combined with **push notifications (incremental rec #1)**, per-wave latency stays low because drivers respond in ~seconds, not poll cycles.

---

## Priority summary

| # | Recommendation | Area | Impact | Effort |
|---|----------------|------|--------|--------|
| 1 | Add ownership checks to `GET /api/payments/*` | Payment | 🔴 Security (IDOR) | Low |
| 2 | Push offers to drivers on creation (SignalR + Expo) | Broadcast | 🔴 Fill rate / latency | Med |
| 3 | Make driver offer polling self-healing on transient errors | Retries | 🔴 Fill rate | Low |
| 4 | Fix/remove dead client calls (`/bookings/stats`, `/upload-item-image`) | Booking | 🟠 Correctness | Low |
| 5 | Single canonical `BookingStatus` contract shared to apps | Booking | 🟠 Future-proofing | Med |
| 6 | Replace 3s payment poll with push + slow safety net | Payment | 🟠 Scale | Med |
| 7 | `_error` dead-letter monitoring/alerting | Retries | 🟠 Observability | Low |
| 8 | Reconciliation job for orphaned/paid + stuck refunds | Payment | 🟠 Money correctness | Med |
| 9 | Wire adaptive offer-expiry config (already in appsettings) | Broadcast | 🟡 Tuning | Low |
| 10 | Tiered scored-wave broadcasting (design proposal) | Broadcast | 🟡 Match quality / fairness | High |
| 11 | Same-unit-of-work for cash payment on booking create | Booking | 🟡 Consistency | Low |
| 12 | Retire misleading `[Obsolete]` labels on live broadcast code | Broadcast | 🟡 Maintainability | Low |

🔴 = do first · 🟠 = high value · 🟡 = when convenient

---

*Generated from a read-only scan. All file/line references were accurate as of the scan date; verify against current `main` before acting. No code was changed.*
