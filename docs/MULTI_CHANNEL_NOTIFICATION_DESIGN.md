# Multi-Channel Notification Design

## Implement Now: FCM Migration

Switch push notifications from Expo to Firebase Cloud Messaging (FCM). No other changes.

### Files to MODIFY

| File | Change |
|------|--------|
| [FirebaseNotificationService.cs](src/Modules/BeeLogistics.Modules.Notification/Infrastructure/Services/FirebaseNotificationService.cs) | Add `IPushNotificationService` to class declaration (implements both IFirebaseNotificationService and IPushNotificationService) |
| [DependencyInjection.cs](src/Modules/BeeLogistics.Modules.Notification/DependencyInjection.cs) | Change `IPushNotificationService` registration from `ExpoPushNotificationService` to `FirebaseNotificationService` |

### Config

Add to `appsettings.json` or environment variables:

```json
{
  "Firebase": {
    "ServiceAccountPath": "/path/to/firebase-service-account.json"
  }
}
```

Or `Firebase:ServiceAccountJson` for inline JSON. Env: `Firebase__ServiceAccountPath`, `Firebase__ServiceAccountJson`.

### Google / Firebase Setup (manual)

1. Firebase Console → create/select project
2. Add iOS and Android apps; note bundle/package IDs
3. Project Settings → Service accounts → Generate new private key → download JSON
4. Store JSON securely (never commit)
5. Cloud Messaging API enabled by default for Firebase

### Mobile App

- Replace Expo tokens with FCM tokens (`@react-native-firebase/messaging` or Expo FCM plugin)
- Re-register via `POST /api/notifications/register-device`
- Existing Expo tokens will fail; Firebase removes invalid tokens automatically

### Docs

- Update [PUSH_NOTIFICATIONS_SETUP.md](PUSH_NOTIFICATIONS_SETUP.md) to note FCM as active push provider
- Update Postman collection with send endpoint

---

## Push Send API Endpoint (Implement Now)

HTTP endpoint for backoffice to send push notifications (test, manual broadcast, admin alerts).

### Endpoint

```
POST /api/notifications/send
Authorization: [Authorize(Policy = "Backoffice")]
```

### Request Body

Provide **one** target type per request:

| Target | Payload fields | Example |
|--------|----------------|---------|
| Single device | `deviceToken` | `{ "deviceToken": "fcm-xxx", "title": "...", "body": "..." }` |
| Multiple devices | `deviceTokens` | `{ "deviceTokens": ["fcm-1", "fcm-2"], "title": "...", "body": "..." }` |
| Single user | `userId` + `appType` | `{ "userId": "guid", "appType": "driver", "title": "...", "body": "..." }` |
| Multiple users | `userIds` + `appType` | `{ "userIds": ["id1", "id2"], "appType": "customer", "title": "...", "body": "..." }` |
| Burst (all of appType) | `appType` + `burst: true` | `{ "appType": "driver", "burst": true, "title": "...", "body": "..." }` |

```json
{
  "title": "Booking Update",
  "body": "Your booking has been dispatched.",
  "data": { "bookingId": "123", "type": "booking-dispatched" },
  "deviceToken": "optional-fcm-token",
  "deviceTokens": ["optional-list"],
  "userId": "optional-guid",
  "userIds": ["optional-list"],
  "appType": "customer|driver",
  "burst": false
}
```

### Response

```json
{
  "devicesSent": 2
}
```

### Files to CREATE

| File | Purpose |
|------|---------|
| `Application/DTOs/SendPushRequestDto.cs` | Request DTO with target options |
| `Application/Commands/SendPushCommand.cs` | MediatR command + handler |
| Add `POST send` action to `DeviceTokensController` or `NotificationsController` | Endpoint |

### Files to MODIFY

| File | Change |
|------|--------|
| `IDeviceTokenRepository` | Add `GetAllByAppTypeAsync(string appType, CancellationToken ct)` for burst |
| `DeviceTokenRepository` | Implement `GetAllByAppTypeAsync` |
| `docs/Bee-Logistics-API-Complete.postman_collection.json` | Add send request |

### Validation

- `title` and `body` required
- `appType` must be `"customer"` or `"driver"` when used
- Exactly one target must be provided; handler returns error if ambiguous or missing

---

## How Other Modules Interact with FCM

### Interaction Paths

| Path | Who | How | Notes |
|------|-----|-----|-------|
| **Device registration** | Mobile apps (customer, driver) | `POST /api/notifications/register-device` | Stores FCM tokens in `DeviceTokens` by `UserId` + `AppType` |
| **Send endpoint** | Backoffice (admin) | `POST /api/notifications/send` | Manual send to device(s), user(s), or burst by appType |
| **IPushNotificationService** | Any module that injects it | `SendToUserAsync(userId, appType, title, body, data)` etc. | When FCM is wired, they automatically use FCM. Same interface as Expo. |
| **CombinedNotificationService** | Uses IPushNotificationService internally | Sends SignalR + push to a user | Registered in DI but **not yet injected** in any handler |
| **INotificationService** | Bookings, DriverOffer, broadcasters | SignalR only | Does **not** use push. Separate from FCM. |

### Current Usage

- **Handlers** (BookingHandlers, DriverOfferHandlers, BeeLogisticsBookingHandlers) use **INotificationService** → SignalR only. No push.
- **Broadcasters** (DriverTopUpEventBroadcaster, BookingPaymentEventBroadcaster) use **INotificationService** → SignalR only. No push.
- **CombinedNotificationService** uses IPushNotificationService but is never injected anywhere, so push is not sent today.

### To Send Push (FCM) from Other Modules

**Option A – Inject IPushNotificationService directly**

```csharp
// e.g. in BookingBroadcastConsumer
private readonly IPushNotificationService _pushService;

await _pushService.SendToUserAsync(driverId.ToString(), "driver", "New booking available", body, data, ct);
```

**Option B – Use CombinedNotificationService (SignalR + push)**

Inject `CombinedNotificationService` instead of `INotificationService` where both real-time and push are needed. Handlers would need to be updated to use it.

**Option C – Adapter (future)**

Wire an adapter so `INotificationService` resolves to a service that does both SignalR and push. Handlers stay unchanged.

### Token Lookup

`IPushNotificationService.SendToUserAsync(userId, appType, ...)` uses `IDeviceTokenRepository.GetByUserIdAsync(userId, appType)` to find tokens. No other module needs to know about FCM tokens; the Notification module handles lookup.

---

## MassTransit Push Notification Service (Design)

Alternative design: other modules **publish messages** via MassTransit instead of injecting `IPushNotificationService`. A consumer in the Notification module handles the message and sends push via FCM.

### Flow

```
Other Module (Bookings, Drivers, etc.)
    │
    │  IPublishEndpoint.Publish(SendPushNotificationRequested(...))
    ▼
MassTransit / RabbitMQ
    │
    │  message routed to consumer
    ▼
SendPushNotificationConsumer (Notification module)
    │
    │  IPushNotificationService.SendToUserAsync(...)  [FCM]
    ▼
Firebase Cloud Messaging
```

### Contract

Create in `BeeLogistics.Shared/Contracts/PushNotificationContracts.cs`:

```csharp
namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Request to send a push notification. Publish from any module;
/// Notification module consumer sends via FCM.
/// </summary>
public sealed record SendPushNotificationRequested(
    string UserId,
    string AppType,       // "customer" | "driver"
    string Title,
    string Body,
    Dictionary<string, string>? Data = null   // Serializable for MassTransit
);

/// <summary>
/// Request to send push to multiple users.
/// </summary>
public sealed record SendPushToUsersRequested(
    IReadOnlyList<string> UserIds,
    string AppType,
    string Title,
    string Body,
    Dictionary<string, string>? Data = null
);

/// <summary>
/// Request to send push to specific device tokens (e.g. broadcast to selected devices).
/// </summary>
public sealed record SendPushToDevicesRequested(
    IReadOnlyList<string> DeviceTokens,
    string Title,
    string Body,
    Dictionary<string, string>? Data = null
);

/// <summary>
/// Request to send push to all users of an app type (burst).
/// </summary>
public sealed record SendPushBurstRequested(
    string AppType,       // "customer" | "driver"
    string Title,
    string Body,
    Dictionary<string, string>? Data = null
);
```

### Consumer

Create `Application/Consumers/SendPushNotificationConsumer.cs` in Notification module:

```csharp
public class SendPushNotificationConsumer : IConsumer<SendPushNotificationRequested>
{
    private readonly IPushNotificationService _pushService;
    private readonly ILogger<SendPushNotificationConsumer> _logger;

    public async Task Consume(ConsumeContext<SendPushNotificationRequested> context)
    {
        var msg = context.Message;
        var count = await _pushService.SendToUserAsync(
            msg.UserId, msg.AppType, msg.Title, msg.Body, msg.Data, context.CancellationToken);
        _logger.LogInformation("Push sent to user {UserId}, {Count} device(s)", msg.UserId, count);
    }
}
```

Add similar consumers for `SendPushToUsersRequested`, `SendPushToDevicesRequested`, `SendPushBurstRequested` (or one consumer that handles a base/union type).

### How Other Modules Use It

**Bookings** – new booking offer to drivers:

```csharp
// In BookingBroadcastConsumer, after creating offers
await context.Publish(new SendPushNotificationRequested(
    driverId.ToString(),
    "driver",
    "New booking available",
    "You have a new booking offer",
    new Dictionary<string, string> { ["type"] = "booking-offer", ["offerId"] = offer.Id.ToString() }
));
```

**Payment** – payment confirmed:

```csharp
await _publishEndpoint.Publish(new SendPushNotificationRequested(
    customerUserId,
    "customer",
    "Payment received",
    "Your payment has been confirmed"
));
```

**Handlers** – after status change:

```csharp
await _publishEndpoint.Publish(new SendPushNotificationRequested(
    customerIdentityUserId,
    "customer",
    "Booking dispatched",
    "Your driver is on the way"
));
```

### Registration

In `Program.cs` (MassTransit config):

```csharp
x.AddConsumer<SendPushNotificationConsumer>();
x.AddConsumer<SendPushToUsersConsumer>();
x.AddConsumer<SendPushToDevicesConsumer>();
x.AddConsumer<SendPushBurstConsumer>();
```

### Files to Create

| File | Purpose |
|------|---------|
| `BeeLogistics.Shared/Contracts/PushNotificationContracts.cs` | Message contracts |
| `Notification/Application/Consumers/SendPushNotificationConsumer.cs` | Consumes `SendPushNotificationRequested` |
| `Notification/Application/Consumers/SendPushToUsersConsumer.cs` | Consumes `SendPushToUsersRequested` |
| `Notification/Application/Consumers/SendPushToDevicesConsumer.cs` | Consumes `SendPushToDevicesRequested` |
| `Notification/Application/Consumers/SendPushBurstConsumer.cs` | Consumes `SendPushBurstRequested` |

### Files to Modify

| File | Change |
|------|--------|
| `IDeviceTokenRepository` | Add `GetAllByAppTypeAsync` for burst consumer |
| `DeviceTokenRepository` | Implement `GetAllByAppTypeAsync` |
| `Program.cs` | Add consumers to MassTransit |

### Benefits

- **Loose coupling**: Modules only reference `BeeLogistics.Shared.Contracts`, not the Notification module
- **Async**: Push is sent asynchronously; publisher is not blocked
- **Retries**: MassTransit retries on failure
- **Outbox-friendly**: If publisher uses DbContext outbox (e.g. Bookings), message is stored in same transaction and published after commit

### How MassTransit Chooses Outbox vs Direct RabbitMQ

| Factor | Outbox (transactional) | Direct to RabbitMQ |
|--------|------------------------|---------------------|
| **Endpoint** | `IPublishEndpoint` | `IBus` |
| **DbContext** | Publish within scope using **BookingsDbContext** (the DbContext configured for outbox) | Different DbContext, or no DbContext, or `IBus` |
| **Transaction** | Publish **before** `SaveChangesAsync()` in same request; outbox stores message in same transaction | No shared transaction with outbox DbContext |
| **When delivered** | After transaction commits; background outbox service picks up and publishes to RabbitMQ | Immediately |

**In this codebase** (from `Program.cs`):

- Outbox: `AddEntityFrameworkOutbox<BookingsDbContext>` – only **BookingsDbContext** participates
- **Bookings handlers** (e.g. `StartBroadcastingBookingCommandHandler`) that use BookingsDbContext + `IPublishEndpoint` + `SaveChangesAsync` → messages go to **outbox**
- **WebhooksController** (Payment module, PaymentDbContext) uses `IBus` explicitly to **bypass outbox** – messages go **direct** to RabbitMQ
- **Drivers module** has its own custom outbox (`IDriverOutboxPublisher` → `drivers.OutboxMessages`), then `DriversOutboxDispatcherService` publishes to MassTransit – those go **direct** (no EF outbox)

**To force direct publish** (no outbox):

```csharp
// Inject IBus instead of IPublishEndpoint
await _bus.Publish(new SendPushNotificationRequested(...), ct);
```

**To use outbox** (transactional with Bookings DB):

```csharp
// In a handler that uses BookingsDbContext
await _publishEndpoint.Publish(new SendPushNotificationRequested(...));
await _repository.SaveChangesAsync();  // Same transaction; message stored in outbox
```

---

## Future Plan: Add Email Channel and SMS

When ready to add a multi-channel send endpoint and Email/SMS, follow this roadmap.

### Phase 2 – Multi-Channel Send Endpoint (Push + Email)

**Goal**: `POST /api/notifications/send` to send via Email and/or Push. Recipients in payload (no resolver).

**Files to create**:

- `Application/Interfaces/IMultiChannelNotificationService.cs`
- `Application/DTOs/MultiChannelNotificationDto.cs` (request, response, `NotificationChannels` enum)
- `Application/Commands/SendMultiChannelNotificationCommand.cs`
- `Application/Handlers/SendMultiChannelNotificationCommandHandler.cs`
- `Infrastructure/Services/MultiChannelNotificationService.cs`
- `Presentation/Controllers/NotificationsController.cs` (or extend `DeviceTokensController`)

**Files to modify**:

- `IDeviceTokenRepository` → add `GetAllByAppTypeAsync(appType, ct)`
- `DeviceTokenRepository` → implement `GetAllByAppTypeAsync`
- `DependencyInjection.cs` → register `IMultiChannelNotificationService`

**Request body** (recipients in payload):

```json
{
  "channels": ["email", "push"],
  "title": "Booking Update",
  "body": "Your booking has been dispatched.",
  "emails": ["customer@example.com"],
  "deviceTokens": ["fcm-token-1"],
  "userId": "guid",
  "appType": "driver",
  "burst": false,
  "data": {}
}
```

**Email channel**: Caller provides `emails`. Use existing `IEmailService`.  
**Push channel**: Caller provides `deviceToken`, `deviceTokens`, or `userId` + `appType`, or `appType` + `burst`.

---

### Phase 3 – Add SMS Channel

**Goal**: Add SMS to the multi-channel send endpoint. Recipients in payload (`phoneNumbers`).

**Files to create**:

- `Application/Interfaces/ISmsService.cs` – `Task<bool> SendAsync(string toPhoneNumber, string message, CancellationToken ct)`
- `Infrastructure/Services/TwilioSmsService.cs` (or other provider)

**Files to modify**:

- `MultiChannelNotificationRequest` → add `PhoneNumbers` (list)
- `MultiChannelNotificationService` → inject `ISmsService`; when `Channels` includes Sms, call for each `PhoneNumber`
- `DependencyInjection.cs` → register `ISmsService`
- `BeeLogistics.Modules.Notification.csproj` → add Twilio (or chosen provider) NuGet

**Config**:

```json
{
  "Twilio": {
    "AccountSid": "...",
    "AuthToken": "...",
    "FromNumber": "+1234567890"
  }
}
```

**Request body** (add for SMS):

```json
{
  "channels": ["email", "sms", "push"],
  "phoneNumbers": ["+1234567890"]
}
```

---

### Future – Design Reference (recipients in payload)

| Channel | Recipient source | Payload field |
|---------|------------------|---------------|
| Email | Caller provides | `emails` |
| Push (device) | Caller provides | `deviceToken` or `deviceTokens` |
| Push (user/users) | Lookup DeviceTokens by userId + appType | `userId` + `appType` or `userIds` + `appType` |
| Push (burst) | Lookup DeviceTokens by appType | `appType` + `burst: true` |
| SMS | Caller provides | `phoneNumbers` |

No IUserContactResolver. Caller supplies all addresses.

---

### Future – IBookingEmailService vs IMultiChannelNotificationService

| Use case | Service |
|----------|---------|
| Templated booking emails (confirmation, dispatched, delivered, etc.) | `IBookingEmailService` |
| Generic/admin send (broadcast, custom message, multi-channel) | `IMultiChannelNotificationService` |

Both use `IEmailService` under the hood.
