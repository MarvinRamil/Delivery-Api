namespace BeeLogistics.Modules.Notification.Application.DTOs;

public record RegisterDeviceTokenDto(
    string DeviceToken,
    string Platform, // "ios" or "android"
    string AppType,   // "customer" or "driver"
    string? TokenType = null // "fcm" (Android) | "apns" (iOS) | "expo"; optional for backward compatibility
);

public record RegisterDeviceTokenResponse(
    bool Success,
    string? Message = null,
    // Vendor-sync hint (Part A5): the token type the backend currently expects for
    // this platform. If it differs from what the client sent, the client should
    // re-acquire and re-register with the expected type.
    string? ExpectedTokenType = null
);

