# Branch changes

**Branch:** `features/6-driver-api-for-retrieving-available-bookings`

Use this file to track changes, notes, and decisions for this branch.

---

## Summary

This branch adds driver API for retrieving available bookings (driver offers), plus docs and tooling for broadcast/offer flow, Docker dev setup, and config (rate limiting, bookings API).

---

## Git commits (this branch)

| Commit   | Date       | Message | Changes |
|----------|------------|--------|--------|
| `9962192` | 2026-02-07 | Remove small | Added docs: BROADCAST_TROUBLESHOOTING.md, DEBUG_PENDING_OFFERS.md, MASSTRANSIT_OUTBOX_DEBUG.md. Modified BookingHandlers.cs (removed small booking restriction). |
| `be47c60` | 2026-02-07 | DOCS upates | Added docs: DRIVER_OFFERS_ENDPOINT_DETAILS.md, DRIVER_OFFERS_FILTERS.md, REDIS_CONNECTION_GUIDE.md, WHEN_OFFERS_ARE_SENT_TO_DRIVERS.md. Modified launchSettings.json. |
| `2dbdd07` | 2026-02-05 | DOcs | Added DRIVER_OFFER_API.md. |
| `e5d4c07` | 2026-02-05 | Driver | Modified DriverOfferHandlers.cs. |
| `96219c1` | 2026-02-05 | feat: add Docker Compose setup for local development with hot reload | Added compose.dev.yml, Dockerfile.dev, README.DEV.md, docs/CONFIGURE_DATABASE.md, docs/DRIVER_OFFER_FLOW.md, docs/REGISTRATION_API.md. Updated .dockerignore. Modified DriverOfferHandlers, BookingDto, IDriverBookingOfferRepository, BookingRepository, DriverBookingOfferRepository, DriverOffersController. |

---

## Session changes (uncommitted / done in this conversation)

- **Lalamove booking broadcast & outbox**
  - `BookingBroadcastConsumer.cs`: Offer expiration set to 10 minutes; log when consumer receives message.
  - `LalamoveBookingHandlers.cs` / `BookingHandlers.cs`: Publish `BookingBroadcastRequested` before `SaveChanges` so bus outbox persists message in same transaction.
  - `Booking.cs`: Set `Id = Guid.NewGuid()` in both constructors so `booking.Id` is valid before outbox publish.
  - `BookingHandlers.cs` (StartBroadcastingBookingCommandHandler): Removed optimistic `BroadcastingToDrivers` before publish (consumer was skipping offer creation). Added `SaveChanges` after Publish; added ILogger and logs.

- **Driver availability**
  - `DriverAvailabilityService.cs`: Log total driver accounts and active count; log per-driver info (Id, Email, FullName, Role, IsActive, HasLocation). When no active drivers, log warning and return empty.

- **Rate limiting**
  - `appsettings.json`: `RateLimit:GeneralEndpointLimit` set to 100000.
  - `RateLimitingMiddleware.cs`: General limit read from `RateLimit:GeneralEndpointLimit`; default 100 when not set.

- **Cursor rules (project rules)**
  - `.cursor/rules/bee-backend-design.mdc`: Always-apply rule encoding backend design from docs (module layering, SOLID, CQRS, Result pattern, MassTransit outbox, naming). References `docs/MODULE_LAYERING.md`, `docs/DEVELOPMENT.md`, `docs/SOLID_REFACTORING_SUMMARY.md`, etc.
  - `.cursor/rules/postman-sync.mdc`: Rule that when any endpoint is added, updated, or removed (or request/response changes), update `docs/Bee-Logistics-API-Complete.postman_collection.json` to match. Applies when editing Controllers or the Postman collection.

- **Changed files (this session)**
  - `src/Modules/BeeLogistics.Modules.Sales/Application/Consumers/BookingBroadcastConsumer.cs`
  - `src/Modules/BeeLogistics.Modules.Sales/Application/Handlers/BookingHandlers.cs`
  - `src/Modules/BeeLogistics.Modules.Sales/Application/Handlers/LalamoveBookingHandlers.cs`
  - `src/Modules/BeeLogistics.Modules.Sales/Domain/Booking.cs`
  - `src/Modules/BeeLogistics.Modules.Sales/Infrastructure/Services/DriverAvailabilityService.cs`
  - `src/BeeLogistics.Api/appsettings.json`
  - `src/BeeLogistics.Api/Middleware/RateLimitingMiddleware.cs`
  - `docs/CHANGELOG.md`
  - `docs/changes/features-6-driver-api-for-retrieving-available-bookings.md` (this file)
  - `.cursor/rules/bee-backend-design.mdc`
  - `.cursor/rules/postman-sync.mdc`

- **Rate limiting off feature** (earlier)
  - `appsettings.json`: Added `Features.DisableRateLimiting` (default `false`).
  - `appsettings.Production.json.example`: Added `Features.DisableRateLimiting` (default `false`). Use `true` to turn off rate limiting.

- **Bookings API docs** (earlier)
  - `docs/BOOKINGS_API_FRONTEND_GUIDE.md`: Pagination for `GET /api/bookings` (query params, PagedResult, example). Date/time picker examples (HTML, JS, React). Summary table updated. (File is untracked.)
  - `docs/LALAMOVE_BOOKING_FLOW.md`: Date/time picker examples for schedule and scheduled date/time (HTML, JS, React). (File is untracked.)

- **Identity** (earlier)
  - JWT now includes `ClaimTypes.Email` in all token-issuing paths (backoffice login, frontend login, OTP registration auto-login, refresh token) so booking APIs can resolve customer by email.

- **Branch changes doc** (earlier)
  - Added `docs/changes/` and this file: `docs/changes/features-6-driver-api-for-retrieving-available-bookings.md`.

- **Untracked docs** (not yet committed)
  - `docs/BOOKINGS_API_FRONTEND_GUIDE.md`
  - `docs/Bee-Logistics-API.postman_collection.json`
  - `docs/LALAMOVE_BOOKING_FLOW.md`
  - `docs/REGISTRATION_API_FRONTEND_GUIDE.md`
  - `docs/changes/` (this folder)

---

## Notes / TODOs

- Broadcast flow still uses obsolete `StartBroadcastingToDrivers()`; future direction is “driver matching” instead of broadcast.
- Hangfire jobs for broadcast queue run every 30 seconds (`process-expired-offers`, `process-driver-queue`).

---

## Related docs

- [Driver Offer API](../DRIVER_OFFER_API.md)
- [Driver Offer Flow](../DRIVER_OFFER_FLOW.md)
- [Bookings API Frontend Guide](../BOOKINGS_API_FRONTEND_GUIDE.md)
- [When Offers Are Sent to Drivers](../WHEN_OFFERS_ARE_SENT_TO_DRIVERS.md)
- [Broadcast Troubleshooting](../BROADCAST_TROUBLESHOOTING.md)
