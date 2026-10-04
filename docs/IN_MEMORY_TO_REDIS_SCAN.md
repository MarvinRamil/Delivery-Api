# In-Memory Caching / State – Redis Migration Scan

Scan date: 2025. Summary of in-memory usage and candidates to move to Redis for multi-instance or persistence.

---

## Already using Redis (or distributed)

| Component | Notes |
|-----------|--------|
| **ICacheService / RedisCacheService** | Uses `IDistributedCache` (Redis when configured; falls back to in-memory). Used by DriverHandlers (wallet/top-up), OtpService, TokenBlacklistService, RateLimitingMiddleware. |
| **RateLimitingMiddleware** | Uses `IDistributedCache` – Redis when configured. |
| **DriverTopUpEventBroadcaster** | Uses Redis Pub/Sub as backplane so all instances see driver top-up events. |
| **Map module** | `IRedisLocationCache` for geospatial driver locations (separate Redis connection). |

---

## Good candidates to move to Redis

### 1. BookingPaymentEventBroadcaster – SSE subscribers

- **Where:** `src/BeeLogistics.Api/Services/BookingPaymentEventBroadcaster.cs`
- **Current:** `ConcurrentDictionary<Guid, List<ChannelWriter<...>>>` per instance. With multiple API instances, each instance only has its own SSE subscribers; payment events published from one instance don’t reach subscribers on another.
- **Recommendation:** Add a Redis Pub/Sub backplane (same pattern as `DriverTopUpEventBroadcaster`): publish to a Redis channel keyed by `CustomerId`; each instance subscribes and forwards to its local SSE/SignalR connections. No DB migration.

### 2. InMemoryLivenessSessionStore – verification sessions

- **Where:** `src/Modules/BeeLogistics.Modules.Verification/Infrastructure/LivenessSessionStore.cs`
- **Current:** In-memory `Dictionary` + timer cleanup. Sessions are lost on restart and not shared across instances.
- **Recommendation:** Implement `IRedisLivenessSessionStore` (or use `IDistributedCache` / Redis hashes) with the same interface; store by `SessionId` with TTL. Enables multi-instance and restarts.

### 3. LocationSanitizer – last update / last location

- **Where:** `src/Modules/BeeLogistics.Modules.Map/Infrastructure/Services/LocationSanitizer.cs`
- **Current:** `ConcurrentDictionary<Guid, DateTime>` (last update time) and `ConcurrentDictionary<Guid, (Lat, Lng, Timestamp)>` (last location) for rate limiting and anomaly detection.
- **Recommendation:** If you run multiple Map/API instances and want consistent throttling and teleport detection across instances, move this state to Redis (e.g. keys like `location:last:{driverId}` with TTL). Optional; single-instance is fine as-is.

---

## Low priority / keep as-is

| Component | Reason |
|-----------|--------|
| **EmailTemplateService** `_templateCache` | Static read-only template cache; small and process-local is fine. |
| **RefundPaymentRequestValidator** `AllowedReasons` | Static readonly set; not cache. |
| **InputSanitizer** `AllowedHtmlTags` | Static readonly set; not cache. |
| **SecurityQuestionsService** `CommonQuestions` | Static readonly list; not cache. |

---

## Summary

- **High value:** BookingPaymentEventBroadcaster (Redis backplane for SSE like DriverTopUp), LivenessSessionStore (Redis or IDistributedCache).
- **Optional:** LocationSanitizer dictionaries → Redis only if you need cross-instance consistency.
- **No change:** General cache and rate limiting already use Redis when configured; static config collections are fine in-memory.
