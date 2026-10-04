using BeeLogistics.Modules.Notification.Application.Interfaces;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Sends push notifications via Firebase Cloud Messaging.
/// Implements both IFirebaseNotificationService and IPushNotificationService for drop-in replacement of Expo.
/// </summary>
public class FirebaseNotificationService : IFirebaseNotificationService, IPushNotificationService
{
    // The Firebase Admin SDK uses a process-wide default app. This service is scoped,
    // so guard initialization so repeated construction never throws "app already exists".
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static bool _initSucceeded;

    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly ILogger<FirebaseNotificationService> _logger;
    private readonly bool _isConfigured;

    public FirebaseNotificationService(
        IDeviceTokenRepository deviceTokenRepository,
        IConfiguration configuration,
        ILogger<FirebaseNotificationService> logger)
    {
        _deviceTokenRepository = deviceTokenRepository;
        _logger = logger;
        _isConfigured = EnsureInitialized(configuration, logger);
    }

    private static bool EnsureInitialized(IConfiguration configuration, ILogger logger)
    {
        // Already initialized (by a prior instance or elsewhere in the process).
        if (_initialized)
            return _initSucceeded;
        if (FirebaseApp.DefaultInstance != null)
        {
            _initialized = true;
            _initSucceeded = true;
            return true;
        }

        lock (InitLock)
        {
            if (_initialized)
                return _initSucceeded;
            if (FirebaseApp.DefaultInstance != null)
            {
                _initialized = true;
                _initSucceeded = true;
                return true;
            }

            try
            {
                var serviceAccountPath = configuration["Firebase:ServiceAccountPath"];
                var serviceAccountJson = configuration["Firebase:ServiceAccountJson"];

                if (!string.IsNullOrEmpty(serviceAccountPath) && File.Exists(serviceAccountPath))
                {
                    FirebaseApp.Create(new AppOptions
                    {
                        Credential = GoogleCredential.FromFile(serviceAccountPath)
                    });
                    _initSucceeded = true;
                    logger.LogInformation("Firebase Admin SDK initialized from file: {Path}", serviceAccountPath);
                }
                else if (!string.IsNullOrWhiteSpace(serviceAccountJson))
                {
                    // Accept either raw JSON (starts with '{') or base64-encoded JSON.
                    // Do NOT silently fall back to the raw value on a decode failure — that
                    // just passes base64 to FromJson and produces a confusing error.
                    var raw = serviceAccountJson.Trim();
                    string? jsonContent;
                    if (raw.StartsWith("{"))
                    {
                        jsonContent = raw;
                    }
                    else
                    {
                        // Strip anything outside the base64 alphabet (whitespace, newlines,
                        // and stray copy/paste artifacts like a trailing '%') so a paste slip
                        // doesn't disable push.
                        var sanitized = System.Text.RegularExpressions.Regex.Replace(raw, "[^A-Za-z0-9+/=]", "");
                        try
                        {
                            var bytes = Convert.FromBase64String(sanitized);
                            jsonContent = System.Text.Encoding.UTF8.GetString(bytes);
                        }
                        catch (FormatException ex)
                        {
                            jsonContent = null;
                            _initSucceeded = false;
                            logger.LogError(ex,
                                "Firebase:ServiceAccountJson is neither raw JSON (starting with '{{') nor valid base64. " +
                                "Firebase push disabled.");
                        }
                    }

                    if (jsonContent != null)
                    {
                        FirebaseApp.Create(new AppOptions
                        {
                            Credential = GoogleCredential.FromJson(jsonContent)
                        });
                        _initSucceeded = true;
                        logger.LogInformation("Firebase Admin SDK initialized from JSON configuration");
                    }
                }
                else
                {
                    _initSucceeded = false;
                    logger.LogWarning("Firebase configuration not found. Push notifications will be disabled.");
                }
            }
            catch (Exception ex)
            {
                _initSucceeded = false;
                logger.LogError(ex, "Failed to initialize Firebase Admin SDK");
            }

            _initialized = true;
            return _initSucceeded;
        }
    }

    public async Task<bool> SendToDeviceAsync(string deviceToken, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning("Firebase not configured. Skipping push notification.");
            return false;
        }

        try
        {
            var message = new Message
            {
                Token = deviceToken,
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = title,
                    Body = body
                },
                Data = ToDataDictionary(data),
                Android = new AndroidConfig
                {
                    Priority = Priority.High
                },
                Apns = new ApnsConfig
                {
                    Aps = new Aps
                    {
                        Sound = "default",
                        Badge = 1
                    }
                }
            };

            var response = await FirebaseMessaging.DefaultInstance.SendAsync(message, ct);
            _logger.LogInformation("Push notification sent successfully. MessageId: {MessageId}", response);
            return true;
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogError(ex, "Failed to send push notification to device {Token}. Error: {Error}", deviceToken, ex.Message);
            
            // If token is invalid, remove it from database
            if (ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument ||
                ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
            {
                await _deviceTokenRepository.DeleteByTokenAsync(deviceToken, ct);
                _logger.LogInformation("Removed invalid device token: {Token}", deviceToken);
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending push notification to device {Token}", deviceToken);
            return false;
        }
    }

    public async Task<int> SendToMultipleDevicesAsync(IEnumerable<string> deviceTokens, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning("Firebase not configured. Skipping push notification.");
            return 0;
        }

        var tokens = deviceTokens.ToList();
        if (!tokens.Any())
        {
            return 0;
        }

        try
        {
            var messages = tokens.Select(token => new Message
            {
                Token = token,
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = title,
                    Body = body
                },
                Data = ToDataDictionary(data),
                Android = new AndroidConfig
                {
                    Priority = Priority.High
                },
                Apns = new ApnsConfig
                {
                    Aps = new Aps
                    {
                        Sound = "default",
                        Badge = 1
                    }
                }
            }).ToList();

            var response = await FirebaseMessaging.DefaultInstance.SendEachAsync(messages, ct);
            
            var successCount = response.SuccessCount;
            var failureCount = response.FailureCount;

            _logger.LogInformation(
                "Sent push notifications to {Total} devices. Success: {Success}, Failed: {Failed}",
                tokens.Count, successCount, failureCount);

            // Remove invalid tokens
            for (int i = 0; i < response.Responses.Count; i++)
            {
                var result = response.Responses[i];
                if (!result.IsSuccess && 
                    result.Exception is FirebaseMessagingException fex &&
                    (fex.MessagingErrorCode == MessagingErrorCode.InvalidArgument ||
                     fex.MessagingErrorCode == MessagingErrorCode.Unregistered))
                {
                    if (i >= 0 && i < tokens.Count)
                    {
                        await _deviceTokenRepository.DeleteByTokenAsync(tokens[i], ct);
                    }
                }
            }

            return successCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error sending push notifications to multiple devices");
            return 0;
        }
    }

    public async Task<int> SendToUserAsync(string userId, string appType, string title, string body, object? data = null, CancellationToken ct = default)
    {
        var deviceTokens = await _deviceTokenRepository.GetByUserIdAsync(userId, appType, ct);
        var tokens = deviceTokens.Select(dt => dt.Token).ToList();

        if (!tokens.Any())
        {
            _logger.LogDebug("No device tokens found for user {UserId} with app type {AppType}", userId, appType);
            return 0;
        }

        // Update last used timestamp
        foreach (var deviceToken in deviceTokens)
        {
            deviceToken.LastUsedAt = DateTime.UtcNow;
            await _deviceTokenRepository.UpdateAsync(deviceToken, ct);
        }
        await _deviceTokenRepository.SaveChangesAsync(ct);

        return await SendToMultipleDevicesAsync(tokens, title, body, data, ct);
    }

    private static Dictionary<string, string>? ToDataDictionary(object? data)
    {
        if (data == null) return null;
        if (data is Dictionary<string, string> dict)
            return dict;
        if (data is IReadOnlyDictionary<string, string> readOnly)
            return readOnly.ToDictionary(kv => kv.Key, kv => kv.Value);
        return data.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(data)?.ToString() ?? "");
    }
}

