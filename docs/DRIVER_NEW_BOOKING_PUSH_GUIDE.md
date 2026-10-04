# Driver “New Booking” Push – Setup Guide (Expo / EAS, No Rebuild)

Use this guide when you want to send push notifications to drivers when a new booking is available. This path uses **Expo Push** and your existing EAS builds—**no driver app rebuild required**.

---

## Can I use EAS for now? Do I need a rebuild?

- **Yes, you can use EAS.** The driver app is already an Expo app; it registers an **Expo push token** with the backend and handles notification taps (e.g. deep link to accept-booking).
- **No rebuild is needed** for the driver app. You only add backend logic to send “new booking” push via the existing Expo push service to already-registered driver tokens.

---

## What’s already in place

| Piece | Status |
|-------|--------|
| Driver app | Uses `expo-notifications` and `getExpoPushTokenAsync()`, registers token at `POST /api/notifications/register-device` with `appType: "driver"`. |
| Backend | Has `IPushNotificationService` → `ExpoPushNotificationService`, `IDeviceTokenRepository`, and device token storage by `UserId` + `AppType`. |
| Deep link | Driver app handles `data.type === 'booking-offer'` and `data.offerId` → navigates to `/accept-booking?offerId=...`. |

---

## What you need to do

### 1. Backend: Send “new booking” push when offers are created

- In **BookingBroadcastConsumer** (after creating driver offers, in both the first-time broadcast and the pulse flow):
  - Inject `IPushNotificationService` (Expo).
  - For each driver in `driversToOffer`, call:
    - `SendToUserAsync(driver.DriverId.ToString(), "driver", title, body, data, ct)`
  - Use a title like **“New booking available”** and a body that fits your UX.
  - In `data`, include at least:
    - `type: "booking-offer"`
    - `offerId` (or whatever the app uses to open the right offer)
    - Optionally `bookingId`, `bookingNumber` for display or deep link.

- Ensure the **Bookings** module references the **Notification** module so the consumer can receive `IPushNotificationService` (it likely already does via the API project).

### 2. (Optional) Expo push access token for production

- Expo’s push API works without a token but has lower rate limits. For production, set:
  - Config: `Expo:AccessToken` or env `EXPO_ACCESS_TOKEN` (from your Expo account).
  - See [EXPO_PUSH_NOTIFICATIONS_SETUP.md](EXPO_PUSH_NOTIFICATIONS_SETUP.md) if you have it in this repo.

### 3. Driver app

- **No code or build changes.** Existing EAS builds keep using Expo push; they already register the token and handle the notification payload.

---

## Checklist (read later)

- [ ] Backend: In `BookingBroadcastConsumer`, inject `IPushNotificationService`.
- [ ] Backend: After creating offers (first-time and pulse), loop over `driversToOffer` and call `SendToUserAsync(driverId, "driver", "New booking available", body, data)` with `type: "booking-offer"` and `offerId` (and any other fields the app expects).
- [ ] (Optional) Set `Expo:AccessToken` or `EXPO_ACCESS_TOKEN` for production.
- [ ] Test: Create a booking so that the driver gets an offer; confirm push is received and tapping it opens the accept-booking screen (no driver app rebuild; use existing EAS build).

---

## Later: switching to Firebase (FCM) for drivers

If you later want the backend to use **Firebase** instead of Expo for driver push:

- You’ll need **FCM device tokens** from the driver app (e.g. via `@react-native-firebase/messaging` or an Expo FCM plugin).
- That typically requires a **new EAS/native build** of the driver app.
- Backend: add Firebase credentials (see plan or “Where to get Firebase credentials” in the main plan) and wire `IFirebaseNotificationService` for `appType == "driver"`.

For “test first on driver app” with no rebuild, stick with Expo as above.
