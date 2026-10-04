# Push Notifications & EAS OTA Setup Guide

This document describes the push notification and EAS OTA update implementation for both customer and driver apps.

> **See also:** [MULTI_CHANNEL_NOTIFICATION_DESIGN.md](MULTI_CHANNEL_NOTIFICATION_DESIGN.md) for the planned multi-channel notification system (Email, SMS, Push) and send endpoint.

## Overview

Both apps now support:
- **Firebase Cloud Messaging (FCM)** for push notifications
- **EAS OTA Updates** for over-the-air updates
- **SignalR** continues to work alongside FCM for real-time notifications when app is open

### Multi-Channel Notification (Planned)

The Notification module will support sending via **Email**, **SMS**, and **Push** through a unified `IMultiChannelNotificationService`. A `POST /api/notifications/send` endpoint will allow backoffice to send to devices, users, or burst by appType. See [MULTI_CHANNEL_NOTIFICATION_DESIGN.md](MULTI_CHANNEL_NOTIFICATION_DESIGN.md) for details.

## Frontend Setup

### Customer App (`bee-customers-app`)

#### Dependencies Added
- `expo-notifications` - Push notification handling
- `expo-device` - Device information
- `expo-updates` - OTA updates
- `eas-cli` - EAS CLI (dev dependency)

#### Configuration Files
- `eas.json` - EAS build and update configuration
- `app.config.js` - Updated with notification permissions and EAS settings

#### Services & Hooks
- `shared/services/notificationService.ts` - Notification service
- `shared/hooks/useNotifications.ts` - Notification hook
- `shared/hooks/useOTAUpdates.ts` - OTA update hook

### Driver App (`bee-drivers-app`)

#### Dependencies Added
- `expo-notifications` - Push notification handling
- `expo-device` - Device information
- `expo-updates` - OTA updates
- `eas-cli` - EAS CLI (dev dependency)

#### Configuration Files
- `eas.json` - EAS build and update configuration
- `app.json` - Updated with notification permissions and EAS settings

#### Services & Hooks
- `shared/services/notificationService.ts` - Notification service
- `shared/hooks/useNotifications.ts` - Notification hook
- `shared/hooks/useOTAUpdates.ts` - OTA update hook

## Backend Setup

### Notification Module (`BeeLogistics.Modules.Notification`)

#### New Components
- **Domain**: `DeviceToken` entity
- **Infrastructure**: 
  - `NotificationDbContext` - Database context
  - `DeviceTokenRepository` - Repository for device tokens
  - `FirebaseNotificationService` - FCM integration service
- **Application**:
  - `RegisterDeviceTokenCommand` - Command to register device tokens
  - `CombinedNotificationService` - Service that uses both SignalR and FCM
- **Presentation**:
  - `DeviceTokensController` - API endpoints for device token management

#### Database Migration
- Migration file: `20250101000000_AddDeviceTokensTable.cs`
- **Note**: Migration has been created but not applied. Run when you have database access:
  ```bash
  dotnet ef database update --project src/Modules/BeeLogistics.Modules.Notification --startup-project src/BeeLogistics.Api
  ```

## Environment Variables

### Frontend Apps

Add to your `.env` files:

```env
# EAS Project ID (get from Expo dashboard after running `eas init`)
EXPO_PUBLIC_EAS_PROJECT_ID=your-project-id

# Firebase configuration (if using Firebase directly)
EXPO_PUBLIC_FIREBASE_API_KEY=your-api-key
EXPO_PUBLIC_FIREBASE_PROJECT_ID=your-project-id
EXPO_PUBLIC_FIREBASE_MESSAGING_SENDER_ID=your-sender-id
EXPO_PUBLIC_FIREBASE_APP_ID=your-app-id
```

### Backend

Add to `appsettings.json` or environment variables:

```json
{
  "Firebase": {
    "ServiceAccountPath": "/path/to/firebase-service-account.json",
    // OR
    "ServiceAccountJson": "base64-encoded-json-or-direct-json-string"
  }
}
```

## Setup Steps

### 1. Firebase Setup

1. Create a Firebase project (or use existing)
2. Add both iOS and Android apps to the Firebase project
3. Download configuration files:
   - iOS: `GoogleService-Info.plist`
   - Android: `google-services.json`
4. For Expo, you'll configure Firebase through EAS when building

### 2. EAS Setup

1. Install EAS CLI globally:
   ```bash
   npm install -g eas-cli
   ```

2. Login to EAS:
   ```bash
   eas login
   ```

3. Initialize EAS in both apps:
   ```bash
   cd bee-customers-app
   eas init
   
   cd ../bee-drivers-app
   eas init
   ```

4. Update `app.config.js` and `app.json` with your EAS project IDs

### 3. Build with EAS

1. Build development builds:
   ```bash
   eas build --profile development --platform ios
   eas build --profile development --platform android
   ```

2. Build production builds:
   ```bash
   eas build --profile production --platform ios
   eas build --profile production --platform android
   ```

### 4. Publish OTA Updates

1. Publish to development channel:
   ```bash
   eas update --branch development --message "Development update"
   ```

2. Publish to production channel:
   ```bash
   eas update --branch production --message "Production update"
   ```

### 5. Backend Configuration

1. Add Firebase Admin SDK service account (required for sending push notifications):
   - Go to [Firebase Console](https://console.firebase.google.com/) → your project → **Project settings** (gear) → **Service accounts**
   - Click **Generate new private key** and download the JSON file
   - **Important:** The `google-services.json` file (used by mobile apps) is **not** the service account. The backend needs the service account JSON which contains `type: "service_account"`, `private_key`, and `client_email`.
   - Save the JSON to `src/BeeLogistics.Api/secrets/firebase-service-account.json` (this folder is gitignored)
   - Or add to `appsettings.json`:
     - `Firebase:ServiceAccountPath`: path to the JSON file (e.g. `secrets/firebase-service-account.json` or absolute path)
     - Or `Firebase:ServiceAccountJson`: the JSON content as string (for Docker/cloud deployments)
   - **Security:** Never commit the service account JSON to version control. If the private key is ever exposed, regenerate it in Firebase Console immediately.

2. Apply database migration:
   ```bash
   dotnet ef database update --project src/Modules/BeeLogistics.Modules.Notification --startup-project src/BeeLogistics.Api
   ```

## Notification Integration

### Where Notifications Are Sent

#### Customer App
- **Booking Status Changes**: When booking status changes (assigned, in-progress, completed, cancelled)
- **Driver Assigned**: When a driver is assigned to a booking
- **Delivery Updates**: When dispatch status changes (picked up, in transit, delivered)

#### Driver App
- **New Booking Offers**: When a new booking offer is created (CRITICAL - replaces polling)
- **Booking Accepted/Rejected**: Confirmation when offer is accepted/rejected
- **Dispatch Status Updates**: When dispatch status changes
- **Manifest Status Changes**: When manifest status is updated

### Backend Integration Example

To send notifications using both SignalR and FCM:

```csharp
// Inject CombinedNotificationService
private readonly CombinedNotificationService _notificationService;

// Send notification
await _notificationService.SendToUserAsync(
    userId: customerId.ToString(),
    appType: "customer", // or "driver"
    method: "DispatchAssigned", // SignalR method name
    signalRData: new { 
        bookingId = booking.Id,
        message = "Driver assigned!" 
    },
    pushTitle: "Driver Assigned",
    pushBody: "A driver has been assigned to your booking",
    pushData: new { 
        type = "dispatch",
        bookingId = booking.Id 
    }
);
```

## Testing

### Test Push Notifications

1. Build and install app on physical device
2. Login to app
3. Device token should be automatically registered
4. Send test notification from backend or Firebase Console

### Test OTA Updates

1. Build production build with EAS
2. Install on device
3. Make code changes
4. Publish OTA update:
   ```bash
   eas update --branch production
   ```
5. Restart app - update should be downloaded and applied

## Troubleshooting

### Push Notifications Not Working

1. Check device token is registered:
   - Check `DeviceTokens` table in database
   - Verify token is present for user

2. Check Firebase configuration:
   - Verify service account JSON is correct
   - Check Firebase project settings

3. Check app permissions:
   - Ensure notification permissions are granted
   - Check device settings

### OTA Updates Not Working

1. Check EAS project ID is set correctly
2. Verify build was created with EAS (not local build)
3. Check update channel matches build profile
4. Ensure `expo-updates` is properly configured

## Next Steps

1. Set up Firebase project and download service account
2. Run `eas init` in both apps
3. Update EAS project IDs in config files
4. Apply database migration when you have DB access
5. Test push notifications end-to-end
6. Test OTA updates

