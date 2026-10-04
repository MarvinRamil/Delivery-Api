# Booking Pulse Mechanism

How a booking gets re-broadcast to drivers until one accepts, why it works the
way it does, and the two options for adding per-booking pulse cadence.

---

## 1. What the "pulse" is

When a customer creates a booking, the system broadcasts it to nearby drivers as
time-limited **offers**. A booking that no driver has accepted yet sits in the
`BroadcastingToDrivers` state. The **pulse** is the recurring background job that
periodically re-broadcasts those waiting bookings — creating fresh offers for
drivers who have *newly* become available — so the job keeps reaching drivers
instead of going stale.

Think of it as a heartbeat for unaccepted bookings: every tick, the system asks
"which bookings are still waiting, and are there new drivers to offer them to?"

---

## 2. Why it exists (the bug it fixed)

The original implementation only created offers at broadcast time. If a booking
was broadcast when **no drivers were available**, nothing was scheduled to retry
it — the booking got stuck in limbo forever, even after drivers came online.

The fix was to make the booking's **state in the database** the trigger, not a
one-shot event:

1. On first broadcast, the booking is moved to `BroadcastingToDrivers`
   **even if zero drivers are available**
   (`BookingBroadcastConsumer.cs` — "set state even with 0 drivers so we can
   pulse later").
2. A recurring pulse job re-derives its worklist from DB state every tick and
   creates offers for any newly-available drivers.

This makes the pulse **self-healing**: a booking with no drivers right now still
appears in the next pulse and is retried the moment a driver becomes available.
Keep this property in mind — it is the whole point of the design, and any change
to "how we decide what to pulse" must preserve it.

---

## 3. How it works today

### 3.1 Components

| Job (Hangfire recurring)        | Method                              | Cadence    | Purpose |
|---------------------------------|-------------------------------------|------------|---------|
| `pulse-broadcasting-bookings`   | `PulseBroadcastingBookingsAsync`    | every 10s  | Re-broadcast waiting bookings; create offers for newly-available drivers |
| `process-driver-queue`          | `ProcessNextDriverInQueueAsync`     | every 30s  | Advance/expire the active offer in a booking's queue |
| `process-expired-offers`        | `ProcessExpiredOffersAsync`         | ~10s       | Expire offers; mark booking `RejectedByAllDrivers` when none remain |

All three live in
`src/Modules/BeeLogistics.Modules.Bookings/Infrastructure/Services/BookingBroadcastQueueService.cs`
and are registered in `src/BeeLogistics.Api/Program.cs`.

There is also an **in-flow pulse** in `BookingBroadcastConsumer.PulseOffersAsync`:
if a `BookingBroadcastRequested` message arrives for a booking already in
`BroadcastingToDrivers`, it pulses immediately instead of re-broadcasting from
scratch.

### 3.2 The pulse tick (`PulseBroadcastingBookingsAsync`)

```
1. Load bookings in BroadcastingToDrivers whose NextPulseAt is due,
   in dispatch order, capped at MaxBookingsPerPulseTick (indexed query).
2. Load bookings in PendingAssignment created in the last 60 min, same
   ordering and cap (safety net for when the initial outbox message was
   never consumed).
3. Re-sort across BOTH lists, then take the cap.
4. For each booking:
     - skip if it already has an Accepted offer
     - promote PendingAssignment -> BroadcastingToDrivers
     - find available drivers
     - create offers ONLY for newly-available drivers
       (exclude drivers who already have an offer of any status;
        do NOT expire existing pending offers)
     - reschedule NextPulseAt using THIS booking's delivery mode
```

**Dispatch order** is `DispatchPriority ↓`, then `COALESCE(NextPulseAt, -infinity)`,
then `CreatedAt` — rank first, then most overdue, then oldest. The COALESCE is
required: PostgreSQL sorts NULLs last on `ASC`, which would invert the "null
NextPulseAt means due now" rule this column was added for.

Step 3 is not redundant. Each query is ordered and capped independently in SQL, so
concatenating them would let a low-rank broadcasting booking beat a high-rank one
still awaiting its first assignment, and the cap would then be decided by which
query a booking came from rather than by rank.

**The cap matters more than the ordering.** Before `MaxBookingsPerPulseTick`
(default 200) the tick iterated the whole list, so ordering only rearranged work
*within* a tick and never decided whether a high-rank booking got served at all.
When the cap binds, the tick logs a warning rather than truncating silently.

Radius, cadence, offer TTL, the no-driver retry interval and the give-up window
are all resolved per delivery mode — see `BOOKING_DELIVERY_MODES.md` §9. A mode
with no overrides configured resolves to exactly the flat `BookingSettings` key,
so `Regular` behaviour is unchanged by any of this.

Offers expire after 10 minutes (minimum 2). The job is guarded with
`[DisableConcurrentExecution]`, which takes a **cluster-wide** distributed lock
via Hangfire storage — so even across multiple API instances, only one pulse tick
runs at a time.

### 3.3 Concurrency safety

There is a unique index on `(BookingId, DriverId)` for offers. If the pulse job
and the broadcast consumer race and both try to create the same offer, the losing
batch hits a `DbUpdateException`, is dropped, and the next pulse recomputes the
exclusion list and creates only the genuinely missing offers.

### 3.4 Is the "what to pulse" query a table scan?

No. `AssignmentStatus` is indexed (`BookingsDbContext.cs`), so
`WHERE AssignmentStatus = 'BroadcastingToDrivers'` is an **index seek** that
returns only the handful of rows currently awaiting a driver — not a scan of the
whole table. When nothing is broadcasting it returns zero rows and the job
early-returns. The per-tick cost scales with the number of *active* bookings, not
total bookings.

The ordered form is served by the partial index
`IX_Bookings_DispatchPriority_NextPulseAt`, filtered on the same
`AssignmentStatus = 'BroadcastingToDrivers'`. `EXPLAIN` against real PostgreSQL
shows an index scan with `Presorted Key: DispatchPriority` and an **Incremental
Sort** for the `CreatedAt` tiebreak — the index covers the filter and the leading
sort key, and only the tail of an already-tiny set needs sorting.

---

## 4. The known limitation

Every booking in `BroadcastingToDrivers` is reprocessed on **every** 10s tick,
regardless of whether it actually needs attention. There is no per-booking
cadence: a 2-second-old booking and a 14-minute-old booking are pulsed at the
same rate. The desired improvement is **per-booking cadence with backoff** — pulse
fresh bookings aggressively and stale ones less often:

```
age < 1 min   -> pulse every 10s   (hot: just created, drivers should see it fast)
age < 5 min   -> every 30s
age < 15 min  -> every 60s
older         -> every 120s        (until the give-up cutoff)
```

Two ways to achieve this follow. **Option A is recommended.**

---

## 5. Option A (recommended): `NextPulseAt` column + due-only query

Add scheduling state to the booking row itself and query only what is *due*.

### 5.1 Schema change (EF migration)

Add to `Booking`:

- `NextPulseAt` (`timestamptz`, nullable) — when this booking should next be pulsed.
- `PulseCount` (`int`, default 0) — optional, useful for backoff and metrics.

Add a composite index:

```
HasIndex(b => new { b.AssignmentStatus, b.NextPulseAt });
```

> Per project convention, this goes through an **EF Core migration**, not raw SQL.

### 5.2 Query change

```sql
WHERE AssignmentStatus = 'BroadcastingToDrivers'
  AND NextPulseAt <= now()
```

Still an index seek, now **due-only** — bookings not yet due are skipped entirely.

### 5.3 Reschedule

After a booking is pulsed:

```
booking.PulseCount++;
booking.NextPulseAt = now + backoff(booking.CreatedAt);
```

Set `NextPulseAt = now` when the booking first enters `BroadcastingToDrivers` so
it is picked up on the next tick.

### 5.4 Why this is the right default

- ✅ Per-booking cadence/backoff — the actual goal.
- ✅ Due-only processing — no reprocessing every booking every 10s.
- ✅ **Self-healing preserved** — the DB is still the source of truth, so the
  original "stuck booking" bug stays fixed. The worklist is re-derived from
  durable state every tick and cannot silently lose a booking.
- ✅ Multi-instance safe with zero extra machinery — Hangfire singleton lock plus
  an indexed query; no shared external index to coordinate.
- ✅ No new runtime dependency and no dual-write consistency surface.

Cost: one migration (two nullable columns + an index).

---

## 6. Option B: Redis ZSET (sorted set) as a due-index

Maintain a Redis **sorted set** `bookings:pulse` where each member is a
`BookingId` and the **score is its next-pulse time** (unix ms). The pulse job
pulls only due members instead of querying the DB for the worklist.

### 6.1 Operations

```
# booking enters BroadcastingToDrivers
ZADD bookings:pulse <now> <bookingId>

# pulse tick (single executor via Hangfire lock)
due = ZRANGEBYSCORE bookings:pulse -inf <now>
for each due booking:
    <existing pulse work: load, skip-if-accepted, offers for newly-available drivers>
    ZADD bookings:pulse <now + backoff(age)> <bookingId>   # reschedule

# accept / rejected-by-all / give-up
ZREM bookings:pulse <bookingId>
```

Use the native `IConnectionMultiplexer` (already available; injected into
`RedisCacheService`) — the `ICacheService` abstraction is get/set/remove only and
cannot do sorted-set operations.

Because the pulse runs under Hangfire's cluster-wide `[DisableConcurrentExecution]`
lock, only one instance drains the set at a time, so no Lua lease / atomic-claim
is required.

### 6.2 The catch: a ZSET is a *derived cache*, and it can drift

Redis is configured as **best-effort** in this project — if no connection string
is set, or the connection fails, the app falls back to in-memory cache
(`Program.cs`). More fundamentally, a ZSET is a cache of the same DB state, and it
can drift from the truth via:

- eviction / `FLUSHDB` / a restart without persistence,
- any new code path that transitions a booking but forgets to `ZADD`/`ZREM`.

When it drifts, the symptom is **identical to the original bug**: a booking
silently stops being offered to drivers. The DB query can't drift because it *is*
the source of truth.

### 6.3 Making Option B safe (if you choose it anyway)

To keep the self-healing property, Redis must be treated as an optimization layer
over the DB, never as the source of truth:

1. **Populate on state entry, independent of driver availability** — `ZADD` the
   moment a booking enters `BroadcastingToDrivers`, even with zero drivers (mirror
   of the fix in Section 2).
2. **Reconciler job (every 60–120s):** run the existing DB scan and
   - `ZADD` any `BroadcastingToDrivers` / recent `PendingAssignment` booking
     missing from the set (covers Redis loss),
   - `ZREM` members whose booking is no longer broadcasting (orphan cleanup,
     accepted-elsewhere, past the give-up cutoff).
3. **No-Redis fallback:** if `IConnectionMultiplexer` is null, use the current
   DB-scan behaviour.

In other words, Option B still needs the DB scan as a safety net — so it adds a
moving part (and a dual-write surface) without removing the thing it was meant to
replace.

---

## 7. Recommendation

| | Option A — `NextPulseAt` column | Option B — Redis ZSET |
|---|---|---|
| Per-booking cadence | ✅ | ✅ |
| Due-only processing | ✅ | ✅ |
| Self-healing (bug stays fixed) | ✅ inherent | ⚠️ only with reconciler |
| Multi-instance | ✅ no extra machinery | ✅ (Hangfire lock) |
| New runtime dependency | none | Redis (for correctness) |
| Consistency surface | none | dual-write DB↔Redis |
| Cost | 1 migration | new service + reconciler + fallback |

**Use Option A.** It delivers the per-booking cadence goal while keeping the
database as the single source of truth, which is exactly what made the original
fix robust. Reserve Redis for data whose loss is harmless (caches), not for
"what work still needs doing."

---

## 8. Key source references

- `src/Modules/BeeLogistics.Modules.Bookings/Infrastructure/Services/BookingBroadcastQueueService.cs`
  — `PulseBroadcastingBookingsAsync`, `ProcessExpiredOffersAsync`, `ProcessNextDriverInQueueAsync`
- `src/Modules/BeeLogistics.Modules.Bookings/Application/Consumers/BookingBroadcastConsumer.cs`
  — first-time broadcast + in-flow `PulseOffersAsync`
- `src/Modules/BeeLogistics.Modules.Bookings/Infrastructure/Repositories/BookingRepository.cs`
  — `GetBroadcastingBookingsAsync`, `GetPendingAssignmentBookingsAsync`
- `src/Modules/BeeLogistics.Modules.Bookings/Infrastructure/BookingsDbContext.cs`
  — index on `AssignmentStatus`, unique `(BookingId, DriverId)` offer index
- `src/BeeLogistics.Api/Program.cs` — Hangfire recurring job registration, Redis config
- `src/BeeLogistics.Shared/Infrastructure/RedisCacheService.cs` — `IConnectionMultiplexer` access
