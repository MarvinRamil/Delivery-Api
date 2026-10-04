# Expo Push Notifications Setup Guide

This guide explains how to set up Expo Push Notifications for the backend.

## Overview

The backend now uses **Expo Push Notification API** instead of Firebase Cloud Messaging (FCM). This simplifies setup and works seamlessly with Expo apps.

## Benefits

- ✅ **No Firebase setup required** - No service account JSON needed
- ✅ **Simpler configuration** - Just an optional access token
- ✅ **Native Expo integration** - Works perfectly with Expo apps
- ✅ **Same functionality** - Push notifications work the same way

## Configuration

### 1. Get Expo Access Token (Optional but Recommended)

1. Log in to your Expo account at [https://expo.dev](https://expo.dev)
2. Navigate to **Account Settings** → **Access Tokens**
3. Create a new access token
4. Copy the token

### 2. Configure Backend

Add the Expo access token to your `appsettings.json`:

```json
{
  "Expo": {
    "AccessToken": "your-expo-access-token-here"
  }
}
```

**Or** set it as an environment variable:

```bash
# Windows PowerShell
$env:EXPO_ACCESS_TOKEN = "your-expo-access-token-here"

# Linux/Mac
export EXPO_ACCESS_TOKEN="your-expo-access-token-here"
```

**Note:** The service will work without an access token, but it's recommended for production to:
- Prevent unauthorized access
- Get better rate limits
- Track usage

### 3. Environment Variables (Alternative)

You can also set the access token via environment variables:

- `EXPO_ACCESS_TOKEN` - Direct environment variable
- `Expo__AccessToken` - ASP.NET Core configuration format

## How It Works

1. **Frontend** gets Expo Push Token using `getExpoPushTokenAsync()`
2. **Frontend** registers token with backend via `/api/notifications/register-device`
3. **Backend** stores token in `DeviceTokens` table
4. **Backend** sends notifications via Expo API: `POST https://exp.host/--/api/v2/push/send`

## API Usage

The backend automatically uses the Expo Push Notification Service when you call:

```csharp
// Inject CombinedNotificationService
private readonly CombinedNotificationService _notificationService;

// Send notification (works for both SignalR and Expo Push)
await _notificationService.SendToUserAsync(
    userId: "user-id",
    appType: "driver", // or "customer"
    method: "NewBookingOffer",
    signalRData: new { bookingId = 123 },
    pushTitle: "New Booking Available",
    pushBody: "You have a new booking offer",
    pushData: new { bookingId = 123 }
);
```

## Testing

### Test Without Access Token

The service works without an access token, but you'll see a warning in logs:

```
Expo Push Notification Service initialized without access token. Consider adding one for production.
```

### Test With Access Token

Once configured, you should see:

```
Expo Push Notification Service initialized with access token
```

## Troubleshooting

### No Notifications Received

1. Check that device tokens are registered in the database
2. Verify the Expo access token is correct (if using one)
3. Check backend logs for errors
4. Ensure the app has notification permissions

### Invalid Token Errors

- Make sure you're using Expo Push Tokens (from `getExpoPushTokenAsync()`)
- Tokens should start with `ExponentPushToken[` or `ExpoPushToken[`
- Old FCM tokens won't work with Expo API

### Rate Limiting

Expo API has rate limits:
- Without access token: Lower limits
- With access token: Higher limits based on your Expo plan

## Migration from Firebase

If you were using Firebase before:

1. ✅ **No database changes needed** - Same `DeviceTokens` table
2. ✅ **Frontend already uses Expo tokens** - No changes needed
3. ✅ **Backend service replaced** - `FirebaseNotificationService` → `ExpoPushNotificationService`
4. ✅ **Same API interface** - `CombinedNotificationService` works the same way

## Next Steps

1. Add Expo access token to `appsettings.json` or environment variables
2. Restart the backend
3. Test push notifications from your app
4. Monitor logs for any issues

