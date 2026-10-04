# Booking Delivery Modes: Regular / Pooling / On-Demand

**Status:** implemented — see issue #67.
Every section is merged. Two things remain open and are decisions rather than code: whether to fund the pooling discount from platform commission (§7.3), and the per-mode tuning values in §9.2/§9.3, which ship at their neutral defaults and are enabled per environment by config alone.

Supersedes `LALAMOVE_BOOKING_FLOW.md`. Companion to `BOOKING_PULSE_MECHANISM.md` (dispatch mechanics) and `BOOKINGS_API_FRONTEND_GUIDE.md` (the client contract).

Nothing is in production yet, so this document describes the intended end state rather than a migration from a live system.

---

## 1. What a delivery mode is

A **delivery mode** is what the customer bought *beyond the delivery itself*.

| Mode | Fare | Dispatch | Who it's for |
|---|---|---|---|
| **Regular** | baseline | baseline | the default |
| **On-Demand** | premium (multiplier on the subtotal) | first in line, searched harder, better drivers first | the customer in a hurry |
| **Pooling** | the rate card, unmarked-up | patient — waits longer, offered first to drivers already heading that way | the customer trading time for money |

Named **On-Demand** rather than "Priority" to match the product, Bee On-Demand. The enum member is `OnDemand`; the wire value is `"OnDemand"`.

`Regular` is ordinal 0 and is what every booking is unless told otherwise. A request that omits `deliveryMode` gets `Regular`.

### 1.1 DeliveryMode is not ServiceType

These are **orthogonal**, and conflating them is the likely misreading. `ServiceType` says *when*; `DeliveryMode` says *what kind*. All six combinations are valid:

| | `Immediate` | `Scheduled` |
|---|---|---|
| **Regular** | go now, normal treatment | book for 3pm, normal treatment |
| **On-Demand** | go now, jump the queue | book for 3pm, and be aggressive about filling it |
| **Pooling** | go now-ish, cheaply | book for 3pm, cheaply |

`ServiceType` stays `{ Immediate, Scheduled }`. It is **not** extended with mode values — that would conflate two axes and collide with the indexed `varchar(20)` column.

---

## 2. One booking = one pickup + one dropoff _(done)_

**Multi-stop within a single booking is not supported.** The platform's model is:

- multiple **independent bookings** per customer
- a driver holding up to **3** bookings at once (`DriverCapacityPolicy.DefaultMaxActive`)

`Booking`'s constructor enforces **exactly two stops: one pickup, one dropoff**. The previous invariant allowed up to 20 stops; that capability was unused and has been removed, along with `AddStop` (which had no callers).

The `DeliveryStop` collection itself **stays** — it carries per-stop status and per-stop proof-of-delivery, which the driver flow depends on. Only the count invariant changed.

**Fare consequence:** `MultiStopFee` is gone from the formula. The old expression was `dropoffCount × AdditionalStopFee`, which charged *every* booking one "additional" stop fee for its only dropoff. Removing it is a **fare correction**, not just dead-code removal. `VehiclePricing.AdditionalStopFee` remains as a column but is no longer read; dropping it would mean touching the version/audit mirror, the admin DTOs and endpoints, and the seeder, for no behavioural gain.

---

## 3. Pooling is opportunistic batching, not a merged trip

This is the most important thing on this page.

A `Pooling` booking is **one independent `Booking`** with its own pickup, dropoff, fare and customer. Pooling does not:

- merge two customers' stops into one route
- split a fare between customers
- relax the one-pickup-one-dropoff invariant
- create a trip, manifest, or batch aggregate
- change anything at accept time

What it *does* is bias **whom the booking is offered to first**: drivers already holding a job that this one is *on the way* for. Since a driver may already hold 3 bookings, a "pooled trip" is just an ordinary driver holding their 2nd or 3rd job — something that happens today with no visibility.

### 3.1 Why this doesn't contradict DriverCapacityPolicy

`DriverCapacityPolicy`'s remarks explicitly decline server-side batching:

> The policy deliberately makes no judgement about whether two jobs are on a compatible route. Distance alone cannot tell a good batch from a bad one — two pickups 2 km apart heading in opposite directions look identical to one that is on the way — and the driver, who can see every address and both fares on the offer, is far better placed to decide.

That objection is about **distance**, and it is correct. Pooling scores **marginal detour** instead:

```
viaB   = |driver → pickup| + |pickup → dropoff| + |dropoff → nextStop|
direct = |driver → nextStop|
detour = viaB - direct - |pickup → dropoff|

score  = clamp(1 - detour / MaxDetourKm, 0, 1)
```

`nextStop` is the next stop the driver **already owes** — which is exactly what makes the discrimination possible.

**Worked example.** Driver at the origin, already owing a stop 5 km due north. The new booking's pickup is 2 km away in both cases:

- *Pickup 2 km north, dropoff 4 km north* — on the way. `viaB = 2 + 2 + 1 = 5`, `direct = 5`, `detour = 5 − 5 − 2 = −2` → clamped, score **1.0**
- *Pickup 2 km south, dropoff 4 km south* — opposite direction. `viaB = 2 + 2 + 9 = 13`, `direct = 5`, `detour = 13 − 5 − 2 = 6` → beyond a 4 km `MaxDetourKm`, score **0.0**

Raw pair distance calls these identical. Detour does not. That is the whole argument.

**Guarantees.** The score only orders who is offered first. It never filters a driver out, never auto-assigns, never relaxes a cap. A driver holding nothing scores 0 — *un-boosted, not penalised* — so an idle nearby driver stays competitive. Worst case is a suboptimal offer *order*, corrected by the next pulse wave; it cannot produce a wrong answer for a customer.

Untouched by pooling: `AcceptBookingOffer`, `DriverCapacityPolicy`, `IDriverCapacityScope`, the `pg_advisory_xact_lock` scope, the `xmin` concurrency guard, and the booking invariant.

**Cost.** Scoring is pure in-process Haversine — deliberately **not** a PostGIS round-trip and **not** a routing API call, because it runs inside candidate selection on a 15-second Hangfire tick across every due booking. Straight-line is adequate here: the score is a *relative ranking*, not a fare. Driver held-legs are fetched in **one** projected query for the whole candidate set, never one per driver.

---

## 4. One creation endpoint _(done)_

There used to be two, and they had drifted badly: a flat pickup/dropoff multipart form that **never set a fare at all**, and a stops-based JSON endpoint (`POST /api/bookings/lalamove`) that **no client ever called**. `EstimatedFare` was assigned in exactly one place in the codebase, on the path nobody used — so every real booking carried a fare of 0 and drivers were quoted ₱0.

Both are now one:

```
POST /api/bookings          — application/json, or multipart/form-data
POST /api/bookings/calculate-fare
```

Multipart carries the payload as a JSON string in a `payload` form field, plus an optional `itemImage` file. `/api/bookings/lalamove` is **deleted**; the "Lalamove" name is gone from the codebase apart from two migration class names, which are primary keys in `__SalesMigrationsHistory` and must not be renamed.

The surviving handler keeps the good parts of both: **idempotency** (a duplicate booking from the same customer within 5 minutes returns the original rather than creating a second delivery), the **confirmation email**, plus service type, payment method, and pricing.

---

## 5. Wire contract _(done)_

`deliveryMode` is a **string** on request payloads:

```json
{
  "vehicleType": "Motorcycle",
  "deliveryMode": "OnDemand",
  "serviceType": "Immediate",
  "stops": [
    { "sequence": 0, "type": "Pickup",  "address": "...", "latitude": 16.61, "longitude": 120.31 },
    { "sequence": 1, "type": "Dropoff", "address": "...", "latitude": 16.62, "longitude": 120.33 }
  ]
}
```

- Accepted values: `"Regular"`, `"Pooling"`, `"OnDemand"`, case-insensitive
- **Absent, `null`, or blank ⇒ `Regular`**
- An unrecognised value is rejected with `Invalid delivery mode: <value>` — never silently downgraded to `Regular`
- **Numeric strings are rejected outright**, including `"0"`. The wire value is a mode *name*; accepting ordinals would couple clients to the enum's declaration order, so reordering it would silently reprice their bookings
- `"Priority"` — the name this mode had during design — is rejected

It must be `string?` on the request DTO, not the enum type: `BookingCreationController` deserialises with its own `JsonSerializerOptions`, which carries no `JsonStringEnumConverter` (the global one is only on the MVC pipeline). An enum member there fails to bind and silently reads as `Regular`. This is the same reason `serviceType` is a string.

On **responses** the enum serialises as a string via the global converter, the same way `status` already does.

**Exactly two stops are required**, one `Pickup` and one `Dropoff`. `calculate-fare` enforces the same rule as booking creation, so a customer can never be quoted a fare they cannot then book.

---

## 6. Distance _(done)_

Fares are distance-based, so distance is the input that matters most.

`RouteDistanceCalculationService` was **in-process Haversine** — straight-line, which underestimates real road distance by roughly 20–40% in a city, so every distance-based fare was systematically low.

**`MapboxRouteDistanceService` now calls the Mapbox Directions API.** The Haversine implementation is retained inside it as the fallback, not deleted. The token already exists server-side (`DriverConfig:Maps:MapboxAccessToken`, from Vault, currently only handed to clients via `/api/config`), so no new secret is needed.

Distance is deliberately **not** taken from the client. It would be the same trust hole as a client-supplied fare — post `distanceKm: 0.1` and ride for base fare. A client-computed distance is display-only.

Implemented:

- **cache** by rounded coordinate pair — a booking is quoted and then created, so the same route is requested at least twice
- **fall back** to Haversine × a configurable road-factor when Mapbox is unreachable, and meter the fallback rate. A booking must never fail because a maps API is down
- **tight timeout**, since this now sits on the create path

Note the pooling detour score (§3.1) deliberately keeps using Haversine — it ranks candidates and runs on a 15-second tick, so per-driver routing calls would be prohibitive and unnecessary.

---

## 7. Pricing _(done)_

**The rate card is the Pooling price. Regular and On-Demand are markups on it.**

That is Lalamove's structure — Pooling is their "Most Affordable Option", Regular is "Standard", Priority is "Fastest", and their published card is a single per-vehicle table with the tier applied as a modifier. It is also what keeps a pooled job from paying the driver less than any other job: nothing is discounted, so nothing prices below the card (see §7.3).

Rates live in `appsettings` under `Pricing:DeliveryModes`, not on `VehiclePricing` rows. A mode markup is a **global commercial knob**, structurally identical to `Pricing:HighDemandSurcharge:MaxMultiplier`; per-vehicle rows are for per-vehicle rates. Putting it on the row would also silently lose it on the `CalculateDefaultFareAsync` fallback path taken when a vehicle type has no pricing row.

```json
"Pricing": {
  "DeliveryModes": {
    "Pooling":  { },
    "Regular":  { "Multiplier": 1.05 },
    "OnDemand": { "Multiplier": 1.25, "FlatFee": 0, "MinFee": 20, "MaxFee": 500 }
  }
}
```

`"Pooling": { }` is present-but-empty on purpose: it documents that the mode exists and *is* the card. Missing config is a **no-op** — a bad deploy costs nothing rather than giving deliveries away. So is *malformed* config: rates are parsed defensively rather than via `GetValue<decimal>`, which throws. This sits on the booking-creation path, so one fat-fingered value would otherwise take out every booking request instead of merely disabling one mode's pricing.

Rates are read the same way on the unknown-vehicle fallback path, so an unrecognised vehicle type is not a way to get the faster tiers for free.

`DiscountRate`/`MaxDiscount` are **retained but unused**. The mechanism stays for the day a mode genuinely has to price below the card; it is still covered by tests, because an untested code path that touches money rots.

### 7.1 Order of operations

```
subtotal     = BaseFare + DistanceFare + WeightSurcharge      (no MultiStopFee — see §2)

Premium      = clamp(subtotal × (Multiplier-1) + FlatFee, MinFee, MaxFee)   ← any mode
Discount     = min(subtotal × DiscountRate, MaxDiscount, subtotal)          ← unused
modeSubtotal = subtotal + Premium - Discount

HighDemand   = modeSubtotal × (multiplier - 1)   ← on the MODE-ADJUSTED amount

TotalFare    = max(0, modeSubtotal + HighDemand + TollFee)
```

**The surge multiplies the mode-adjusted fare, and that is a deliberate reversal.** The earlier rule surged the pre-markup subtotal so that "a 3× surge must not also triple the On-Demand fee". That preserved the premium in pesos but shrank it in proportion: at a 2× surge a +25% On-Demand booking was only +12.5% dearer than Regular, and Pooling only −7.4% cheaper instead of −15%. The tiers converged exactly when speed was worth the most to the customer and the trip cost the most to serve. Surging `modeSubtotal` holds the spacing at any multiplier — pinned by `The_mode_spacing_survives_any_surge`, which replaced the test asserting the opposite.

Note also that Lalamove publishes every service modifier as a percentage — round trip +70–90%, high demand up to 300% — never as an additive fee.

### 7.2 Worked example

Motorcycle, 8 km, from the shipped card (`BaseFare ₱49`, `PerKm0to5 ₱6`, `PerKmAbove5 ₱5`):

```
DistanceFare = 5×6 + 3×5 = ₱45
subtotal     = 49 + 45   = ₱94        ← this is the Pooling price

Pooling   → ₱94.00
Regular   → 94 + 94×0.05                    = ₱98.70
OnDemand  → 94 + clamp(94×0.25, 20, 500)    = ₱117.50
```

Spacing under surge, the property the change exists for:

| | no surge | 2× | 3× |
|---|---|---|---|
| Pooling | ₱94.00 | ₱188.00 | ₱282.00 |
| Regular | ₱98.70 | ₱197.40 | ₱296.10 |
| On-Demand | ₱117.50 | ₱235.00 | ₱352.50 |

Ratios stay 1.00 / 1.05 / 1.25 at every multiplier.

The markup appears in the `Breakdown` string named after the mode that earned it — `On-Demand Premium`, `Regular Service` — because a fixed "Priority Fee" label would now show on a Regular booking and read as an error to the customer paying it. Pooling has no line at all rather than a ₱0.00 one. `Total:` is always last. `TotalFare` is floored at zero, so no configuration can produce a negative fare, which would otherwise propagate into the driver's earnings quote and the cash they collect.

On the wire the markup is still called `priorityFee`. That name is now actively wrong — it carries the markup for *any* mode, and in Lalamove's own API `priorityFee` means a customer-paid **tip to the driver**, which is a different thing entirely. Renaming a persisted `numeric(10,2)` column and a wire field is a wide mechanical change and was deliberately not mixed into the MR that changed prices; only the customer-visible label was fixed here.

### 7.3 Driver payout

The platform takes **5%**; the driver keeps **95%**. That is the commitment made to drivers, and both sides of the system read the same key so they cannot drift: the offer-time quote (`GetPendingOffers`) and the settlement (`EarningCreditConsumer`) both resolve `DriverWallet:PlatformCommissionRate`, defaulting to `PlatformCommissionDefaults.Rate = 0.05m`. Overridable from Vault as `DriverWallet__PlatformCommissionRate`.

> The rate is a **fraction**: `0.05` is 5%. Startup validation refuses anything outside 0–1, because the natural thing to type for "five percent" is `5`, Vault outranks every default, and `EarningsSplit` would then throw at the point of use — presenting as every driver's offer list returning 500 and completed bookings never crediting, rather than as a bad deploy.

`OfferEarningsCalculator` splits the **gross fare**, so no code change is needed: a marked-up booking's larger gross flows through the unchanged `EarningsSplit` and the driver's net rises automatically.

| | Fare (8 km) | Driver keeps 95% |
|---|---|---|
| Pooling | ₱94.00 | ₱89.30 |
| Regular | ₱98.70 | ₱93.77 |
| On-Demand | ₱117.50 | ₱111.63 |

**The pooled pay cut is resolved.** Under the previous model Pooling was a −15% discount off Regular, so a pooled job paid the driver ₱75.91 against ₱89.30 — the open risk at the end of #67, whose only proposed fixes were funding the discount from commission or accepting it. Making the card the Pooling price removes it structurally: no mode prices below the card, so no mode underpays relative to another. Pinned by `No_mode_prices_below_the_rate_card`.

One consequence remains, surfaced rather than hidden: **the platform takes its 5% of the markup too**, so payout is proportional rather than full pass-through. Full pass-through would need an `EarningsSplit` change, and that file's own docs explain why a second fare formula there is dangerous (#28).

---

## 8. Fare trust _(done)_

`estimatedFare` and `priorityFee` sent by a client are not trusted. The server recomputes the fare in `CreateBookingCommandHandler` and persists its own number — including the amount on the cash-on-delivery `Payment`, which is what a driver physically collects. The client still sends the quote it displayed, purely so drift can be measured.

The rule is asymmetric, because a symmetric one breaks honest bookings:

| Situation | Outcome |
|---|---|
| server ≈ quote (within tolerance) | proceed on the server fare |
| client quoted **more** than the server computes | proceed on the cheaper server fare; never reject |
| server materially **dearer** | `400` — *"The fare has changed since your quote (now ₱X). Please review the price and try again."* |
| no quote presented (`<= 0`) | proceed on the server fare |

`tolerance = max(ToleranceAbsolute, serverFare × ToleranceRate)`, default `max(₱25, 10%)`.

Only the *rejection* is gated, behind `Pricing:FareTrust:Enforce`, which ships **`false`** — drift is logged and metered while the server fare is used anyway. Benign drift is expected: the high-demand surcharge reads `UtcNow`, so confirming at 06:59:59 can cross into a peak window between quote and submit.

**Client obligation:** keep sending the quote you displayed, stop expecting `priorityFee` to be honoured, and handle the re-quote `400` by re-quoting rather than retrying.

---

## 9. On-Demand dispatch — four levers _(done)_

All tuning lives under `BookingSettings:DeliveryModes:{mode}:{key}`, resolved by `Application/Services/DeliveryModeSettings.cs` with a three-step fallback:

```
BookingSettings:DeliveryModes:{mode}:{key}  →  BookingSettings:{key}  →  the caller's default
```

The middle step is the point. **Regular ships with no overrides, so every Regular value resolves to exactly the flat key it resolved to before any of this existed** — which is what makes "modes change dispatch" a tuning change rather than a rewrite of how every booking is dispatched. Overrides are *deltas*, not replacement blocks: a mode that only widens its radius keeps the flat cadence.

Missing, blank and unparseable values are all treated alike as "not configured". Every caller sits on the dispatch path — inside the Hangfire pulse tick or the broadcast consumer — and `GetValue<T>` throws on a value it cannot convert, so without that fallback one malformed entry would stop dispatch for *every* booking rather than mis-tuning one knob.

### 9.1 Cross-booking ordering _(done)_

The only lever that decides *whether* a mode gets served rather than merely how. `Booking.DispatchPriority` (`OnDemand 100`, `Regular 50`, `Pooling 10`) orders four worklists:

| Query | Was | Now |
|---|---|---|
| `GetDueForPulseAsync` | unordered, uncapped | `DispatchPriority ↓`, `COALESCE(NextPulseAt)`, `CreatedAt`; capped |
| `GetBroadcastingBookingsAsync` | unordered, uncapped | same; capped |
| `GetPendingAssignmentDueForDispatchAsync` (new) | — | same; capped, and bounded to recent bookings in SQL |
| `GetPendingOffersForDriverAsync` | `OfferedAt ↑` | `DispatchPriority ↓`, `OfferedAt ↑` |

`GetPendingAssignmentBookingsAsync` **keeps** its newest-first, uncapped shape: it backs an operator listing, not dispatch. The pulse job got its own query instead, which also moved the 60-minute recency bound from an in-memory `Where` into the query and dropped the `Customer` join the job never reads. Reordering the listing to suit the job would have silently rearranged a back-office screen that never asked for it.

**The cap is what makes ordering matter.** `PulseBroadcastingBookingsAsync` had no `.Take()` anywhere and iterated the whole concatenated list, so ordering alone only rearranged work *within* a tick — it never decided whether a high-rank booking got served. `BookingSettings:MaxBookingsPerPulseTick` (default 200) is the ceiling, and the tick logs a warning when it binds rather than truncating silently.

Both source queries are ordered and capped independently in SQL, so the tick **re-sorts across both** before applying the cap. Concatenating them would let a low-rank broadcasting booking beat a high-rank one still awaiting its first assignment, and the cap would then be decided by which query a booking came from.

A derived `int` column rather than an ordering on the enum, because **enums are stored as strings**: `ORDER BY "DeliveryMode"` sorts alphabetically — `OnDemand < Pooling < Regular`, putting Regular last and Pooling ahead of it — which is silently wrong. Ranks have gaps so a future tier needs no data migration. The mapping lives in `Domain/DeliveryModeRanking.cs` rather than beside the wire parsing in `Application/Services/DeliveryModePolicy.cs`, because `Booking`'s constructor needs it and Domain must not reference Application.

`ThenBy(b => b.NextPulseAt ?? <utc min>)`, never `ThenBy(b => b.NextPulseAt)`: PostgreSQL sorts NULLs **last** on `ASC`, the exact inverse of the "null means due now" meaning the column was given and that the `WHERE` clause relies on. Two kinds of row reach it — bookings already broadcasting when `AddBookingPulseSchedule` added the column nullable and unbackfilled, and every `PendingAssignment` booking, which has never been pulsed at all. (A booking that enters `BroadcastingToDrivers` *after* that migration always has a value: `StartBroadcastingToDrivers` stamps one.) The sentinel must carry `DateTimeKind.Utc` — the column is `timestamptz` and Npgsql refuses to write an Unspecified `DateTime` to one, so a bare `DateTime.MinValue` would throw in production while passing in-memory tests.

Verified against real PostgreSQL rather than the in-memory provider:

- the COALESCE renders as `TIMESTAMPTZ '-infinity'`, and NULL-pulsed bookings do sort first within their rank;
- `EXPLAIN` uses `IX_Bookings_DispatchPriority_NextPulseAt` with `Presorted Key: DispatchPriority` and an **Incremental Sort** for the tail. The index serves the filter and the leading sort key; the `CreatedAt` tiebreak costs a sort node on a set already scoped to one fast-changing assignment status, which is accepted deliberately — FIFO within a rank is worth more than saving a sort on a handful of rows;
- the inbox join translates to a plain `INNER JOIN … ORDER BY "DispatchPriority" DESC, "OfferedAt"`.

The inbox needs an explicit join because `DriverBookingOffer` has **no navigation** to `Booking` (`HasOne<Booking>().WithMany()` with no property). Denormalising `DispatchPriority` onto the offer row was the alternative and was rejected: a second copy of derived data that can drift, to save a join on an endpoint returning at most ten rows behind an existing index.

### 9.2 Per-mode dispatch values _(mechanism done, values not shipped)_

Every knob below is mode-aware in code. **No On-Demand or Pooling override is configured**, so behaviour today is identical to before — which is what made the ordering change reviewable in isolation. The values are the intended starting point for a separate config-only change.

| Knob | Regular | On-Demand | Pooling |
|---|---|---|---|
| `H3InitialSearchRadiusKm` | 3 | **7** | 3 |
| `H3MaxSearchRadiusKm` | 10 | **12** | 10 |
| Pulse cadence, fresh → stale (s) | 10/30/60/120 | **5/10/20/45** | 10/**60**/60/120 |
| Offer TTL, many/few drivers (min) | 2/5 | **1/2** | 2/**8** |
| `NoDriverRetryMinutes` | 5 | **2** | 5 |
| `ScheduledClaimLeadMinutes` | 45 | 45 | 45 |
| `MaxBroadcastMinutes` (give-up) | 10 | 10 | **25** |

On-Demand's "aggressive from t=0" *is* the wider initial radius: the radius already ramps with booking age, so On-Demand simply starts where Regular ends up. There is no separate code path.

> **Do not set an offer TTL below `many = 1` / `few = 2`.** `OfferExpiry` floors at 1 minute (`OfferExpiry.MinimumMinutes`), but two separate expiry paths — `GetExpiredOffersAsync` and `ProcessNextDriverInQueueAsync` — each apply a 30-second grace, so a 1-minute TTL already leaves a driver only ~30 seconds of real decision time.

> **Inbox starvation.** Ranking the driver's inbox can bury an older low-rank offer for a driver polling with a small `limit` while higher-rank offers keep arriving. Bounded by the offer TTL; watch `offers_expired` by mode.

### 9.3 Better drivers first _(done)_

`AvailableDriver.RankingScore(distanceKm, bonus)` is `ProximityScore(distanceKm) + bonus`, where `bonus` is mode-specific, normalised to `[0,1]`, and weighted from config. A bonus of exactly 0 makes it **bit-identical** to `ProximityScore` — adding zero to a finite double is exact — which is how Regular ranking is *provably* unchanged rather than merely believed to be.

Both candidate paths (H3 index, fallback scan) previously ranked with separately written but identical code. They now share one `Rank` helper. That duplication was the real hazard: which path runs depends on whether Redis returned candidates, so a bias landing on one and not the other would have presented as a flaky market rather than a bug.

- **On-Demand** biases on **recent offer-acceptance rate**, not star rating. Ratings live in the Rating module, which Bookings does not reference (Rating references Bookings), and `IDriverRatingRepository` has no batch method. Acceptance rate is available in-module in one grouped query and is the better predictor of "will this driver take the job" — and the fairer thing to rank on, since answering offers is a direct choice in a way a rating is not. A driver below `QualityScoreMinSamples` offers scores a neutral **0.5** rather than being ranked into invisibility on their first shift.
- **Pooling** biases on the detour score in §3.1.

**Both weights ship at 0.0, and at 0 the code issues no query at all** — `BuildBonusAsync` returns null before touching the database. Inert means inert, not "computes a zero".

| Knob | Default | Meaning |
|---|---|---|
| `DeliveryModes:OnDemand:QualityScoreWeight` | `0.0` | weight on acceptance rate |
| `DeliveryModes:OnDemand:QualityScoreWindowDays` | `14` | how far back responsiveness is measured |
| `DeliveryModes:OnDemand:QualityScoreMinSamples` | `5` | offers below which the neutral score applies |
| `DeliveryModes:Pooling:PoolingScoreWeight` | `0.0` | weight on route compatibility |
| `DeliveryModes:Pooling:MaxDetourKm` | `4.0` | detour at which the bonus reaches 0 |
| `DeliveryModes:Pooling:PoolingCandidateLimit` | `45` | how many proximity-nearest candidates get scored |

Calibration: `ProximityScore` is `1/(1+km)`, so at typical urban distances a bonus of `0.35` is worth roughly "3 km closer". Raise weights slowly while watching pickup times; over-weighting makes the market prefer convenient drivers over near ones.

**Two queries, never N+1.** `GetHeldLegsForDriversAsync` and `GetResponseStatsAsync` each take the whole candidate set and return once. Verified against real PostgreSQL: the held-legs query renders as a single `JOIN LATERAL` over `DeliveryStops`, and the responsiveness query as a single `GROUP BY` with `count(*) FILTER (WHERE "Status" = 'Accepted')`. Pending offers are excluded from both counts — a driver cannot have declined an offer they are still looking at, and counting it against them would penalise whoever was asked most recently.

Detour scoring is `O(candidates × held legs)` of in-process trigonometry on a 15-second tick, hence `PoolingCandidateLimit`: a driver ranked 60th on proximity will not be offered the booking however well it fits their route.

### 9.4 Higher payout _(done)_

Automatic via the unchanged `EarningsSplit` — see §7.3.

---

## 10. Pooling's "wider pickup window"

Not a new field. `ScheduledPickupWindow` already exists as free-text `varchar(50)` that **nothing reads**, and a second unread field would be worse than none. Concretely, the wider window *is* the dispatch knobs:

- `MaxBroadcastMinutes = 25` (vs 10) — a pooled booking keeps looking for a compatible driver for 25 minutes instead of giving up at 10. This is the server-side contract behind any customer-facing promise.
- `OfferExpirationFewDriversMinutes = 8` — a batching decision genuinely needs more thought than a solo job.
- Slower warm pulse cadence — fewer wasted waves for a deliberately patient booking.

A customer-facing "we'll pick up within ~45 minutes" is a **client-side presentation** of these numbers.

> **Implementation note.** `MaxBroadcastMinutes` is read in **three** places — `AdvanceBroadcastAsync` (whether to request another wave), `ComputeGiveUpDeadline` (when to give up entirely), and `ComputeSearchRings` in `DriverAvailabilityService` (the denominator of the radius ramp). All three are mode-aware, and they must stay that way together. If one reverts to the flat key, a Pooling booking's window, its re-advance schedule and its radius growth disagree: it stops being re-advanced at 10 minutes while claiming a 25-minute window, then sits there until a backstop tick catches it. No exception and no log — just a booking that quietly stops trying. That is why `BookingBroadcastQueueService` reads it through a single `MaxBroadcastMinutesFor` accessor rather than inline at each site.

---

## 11. What is deliberately not built

So the next reader doesn't assume otherwise:

- **No multi-stop bookings.** Exactly one pickup and one dropoff. Multiple deliveries are multiple bookings.
- **No fare splitting.** Pooled customers each pay their own fare at the card rate.
- **No merged routes or route optimisation.** Nothing re-sequences across bookings or computes an optimal tour.
- **No trip / manifest / batch aggregate**, and no driver-facing trip screen. A pooled driver sees two ordinary jobs.
- **No pickup-window enforcement.** Nothing anywhere enforces a promised pickup time.
- **No forced batching.** The server never assigns a batch; it only reorders who is asked first.
- **No guaranteed second job.** Pooling is best-effort. A pooled booking that never finds a compatible driver is served as an ordinary one — the self-healing invariant in `BOOKING_PULSE_MECHANISM.md` §2 still holds.
- **No client-supplied distance or fare.** Both are computed server-side.
- **No idempotency key.** Duplicate protection is heuristic — same customer, same route, same vehicle, within 5 minutes. A client-supplied key is a real gap, tracked separately.

---

## 11b. Where the configuration actually lives

Worth stating plainly, because the obvious answer is wrong: **`appsettings.json` is gitignored**, so it never reaches a built image — CI builds from a git checkout, and `appsettings.Production.json.example` is not a filename the config loader looks for. Both files are documentation of the shape, nothing more.

Every mode value is passed as a `-e` environment variable by the deploy jobs in `.gitlab-ci.yml`, each as `${CI_VARIABLE:-default}`. So retuning is a GitLab CI variable change plus a re-run of the deploy job — a config change, not a code change — and the committed defaults are what you get if nobody sets anything.

**Pricing is live in every environment; dispatch tuning is not.** Those two started out with the same posture and deliberately diverged: the tiering in §7 is the intended price list, whereas the dispatch knobs in §9.2 and the ranking weights in §9.3 are still being calibrated.

| Environment | Pricing (§7) | Dispatch tuning (§9.2) | Ranking weights (§9.3) |
|---|---|---|---|
| dev | **live** — Regular +5%, On-Demand +25% | **enabled** | **0.35** |
| staging (UAT) | **live** | **enabled** | **0.35** |
| production | **live** | neutral — resolves to the flat keys | **0.0** — no bias, no query |

> **Two things are not config-gated at all.** Cross-booking ordering and driver-inbox ordering key off the persisted `Booking.DispatchPriority` column, not a setting — there is no weight or flag in front of `OrderByDescending(b => b.DispatchPriority)`. So even with the dispatch knobs neutral, an On-Demand booking is pulsed before a Regular one and appears higher in a driver's inbox. That is intended, and safe because the column shipped with a `50` backfill so no in-flight booking was deprioritised.

> **The commission rate is validated at startup**, unlike the pricing multipliers. `DriverWallet__PlatformCommissionRate` is a fraction (`0.05` = 5%) and a value outside 0–1 now fails the deploy. The mode multipliers have no such guard: a `Multiplier` of `125` instead of `1.25` would be a 12,400% markup that boots cleanly. Worth extending the same validation.

Secrets remain in Vault `bee/config`, which is the highest-precedence source; none of the mode values are secret, so they stay in CI where they are reviewable.

---

## 12. Observability

| Metric | Why |
|---|---|
| `FareRecomputeDrift` (histogram, ₱; tags `outcome`, `mode`) | decides when `Pricing:FareTrust:Enforce` can be turned on. Do not enable until it is boring |
| `bee.bookings.created_by_mode` (counter, tag `mode`) | adoption, and the denominator for every other per-mode number here |
| `bee.matching.pooling_bias_applied` (counter) | candidates boosted by the detour score. A flat zero after tuning means the weight, the detour ceiling, or supply — not necessarily a bug |
| `bee.bookings.offers_rejected` / `offers_expired`, tagged `mode` | On-Demand inbox starvation, and a check on the §7.3 claim that no mode now underpays: a pooled reject rate above Regular's would mean the pay argument is wrong somewhere |
| Mapbox fallback rate | how often fares are being computed from a straight line |

---

## 13. Related documents

| Doc | Relevance |
|---|---|
| `BOOKING_PULSE_MECHANISM.md` | authoritative dispatch mechanics; §2 self-healing and §3.3 concurrency are binding constraints |
| `BOOKINGS_API_FRONTEND_GUIDE.md` | the client contract — request/response shapes and enums |
| `BREAKING_CHANGES.md` | the creation-endpoint consolidation and fare trust both belong here |
| `BOOKING_SAGA_DESIGN.md` | proposed, unimplemented; warns dispatch state is owned in four places. Mode ordering adds a fifth concern — noted, not refactored |
| `MIGRATION_GUIDE.md` | EF migration workflow (migrations, never raw SQL) |
| `LALAMOVE_BOOKING_FLOW.md` | superseded by this document |

## 14. Companion issues

| Repo | Issue |
|---|---|
| `bee-driver` | #21 — show delivery mode on offers |
| `bee-customer` | #22 — mode selector, fare preview, server-authoritative fare handling |
| `back-office` | #2 — surface mode on bookings; decide on per-mode rate config |
