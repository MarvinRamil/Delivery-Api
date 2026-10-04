using BeeLogistics.Modules.Notification.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Service for sending push notifications via Expo Push Notification API
/// </summary>
public class ExpoPushNotificationService : IPushNotificationService
{
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly ILogger<ExpoPushNotificationService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string? _accessToken;
    private readonly bool _isConfigured;

    private const string ExpoPushApiUrl = "https://exp.host/--/api/v2/push/send";

    public ExpoPushNotificationService(
        IDeviceTokenRepository deviceTokenRepository,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ExpoPushNotificationService> logger)
    {
        _deviceTokenRepository = deviceTokenRepository;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");

        // Get Expo access token from configuration (optional but recommended)
        _accessToken = configuration["Expo:AccessToken"] ?? 
                      configuration["Expo__AccessToken"] ??
                      Environment.GetEnvironmentVariable("EXPO_ACCESS_TOKEN");

        if (!string.IsNullOrEmpty(_accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            _isConfigured = true;
            _logger.LogInformation("Expo Push Notification Service initialized with access token");
        }
        else
        {
            _isConfigured = true; // Expo API works without token, but less secure
            _logger.LogWarning("Expo Push Notification Service initialized without access token. Consider adding one for production.");
        }
    }

    public async Task<bool> SendToDeviceAsync(string deviceToken, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning("Expo Push Notification Service not configured. Skipping push notification.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            _logger.LogWarning("Device token is empty. Skipping push notification.");
            return false;
        }

        if (!IsExpoPushToken(deviceToken))
        {
            // A non-Expo token (e.g. native FCM/APNs) can't be delivered via Expo and
            // would return a 400. Skip it rather than fail the request.
            _logger.LogWarning("Expo: token is not a valid Expo push token; skipping.");
            return false;
        }

        try
        {
            var message = CreateExpoMessage(deviceToken, title, body, data);
            var response = await SendPushNotificationAsync(new List<ExpoPushMessage> { message }, ct);

            if (response?.Data?.Any() == true)
            {
                var result = response.Data.First();
                if (result.Status == "ok")
                {
                    _logger.LogInformation("Push notification sent successfully. Ticket: {Ticket}", result.Id);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Failed to send push notification. Status: {Status}, Error: {Error}", 
                        result.Status, result.Message);
                    return false;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notification to device {DeviceToken}", deviceToken);
            return false;
        }
    }

    public async Task<int> SendToMultipleDevicesAsync(IEnumerable<string> deviceTokens, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning("Expo Push Notification Service not configured. Skipping push notifications.");
            return 0;
        }

        var candidates = deviceTokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        // Only send well-formed Expo tokens. A native FCM/APNs token here (e.g. one
        // mis-flagged as "expo") would make Expo reject the ENTIRE batch with a 400.
        var tokens = candidates.Where(IsExpoPushToken).ToList();
        var skipped = candidates.Count - tokens.Count;
        if (skipped > 0)
        {
            _logger.LogWarning(
                "Expo: skipped {Skipped} token(s) that are not valid Expo push tokens (cannot be delivered via Expo).",
                skipped);
        }
        if (!tokens.Any())
        {
            return 0;
        }

        try
        {
            // Expo allows up to 100 messages per request
            var messages = tokens.Select(token => CreateExpoMessage(token, title, body, data)).ToList();
            var successCount = 0;

            // Split into batches of 100
            for (int i = 0; i < messages.Count; i += 100)
            {
                var batch = messages.Skip(i).Take(100).ToList();
                var response = await SendPushNotificationAsync(batch, ct);

                if (response?.Data != null)
                {
                    successCount += response.Data.Count(r => r.Status == "ok");

                    // Log errors for failed notifications
                    var failures = response.Data.Where(r => r.Status != "ok").ToList();
                    if (failures.Any())
                    {
                        _logger.LogWarning("Failed to send {Count} push notifications. Errors: {Errors}",
                            failures.Count,
                            string.Join(", ", failures.Select(f => f.Message)));
                    }
                }
            }

            _logger.LogInformation("Sent push notifications to {Total} devices. Success: {Success}", 
                tokens.Count, successCount);
            return successCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notifications to multiple devices");
            return 0;
        }
    }

    public async Task<int> SendToUserAsync(string userId, string appType, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogWarning("Expo Push Notification Service not configured. Skipping push notification.");
            return 0;
        }

        try
        {
            var deviceTokens = await _deviceTokenRepository.GetByUserIdAsync(userId, appType, ct);
            if (!deviceTokens.Any())
            {
                _logger.LogDebug("No device tokens found for user {UserId} with app type {AppType}", userId, appType);
                return 0;
            }

            var tokens = deviceTokens.Select(dt => dt.Token).ToList();
            return await SendToMultipleDevicesAsync(tokens, title, body, data, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notification to user {UserId}", userId);
            return 0;
        }
    }

    private static bool IsExpoPushToken(string token) =>
        token.StartsWith("ExponentPushToken[", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("ExpoPushToken[", StringComparison.OrdinalIgnoreCase);

    private ExpoPushMessage CreateExpoMessage(string to, string title, string body, object? data = null)
    {
        var message = new ExpoPushMessage
        {
            To = to,
            Title = title,
            Body = body,
            Sound = "default",
            Priority = "default",
            ChannelId = "default"
        };

        // Convert data object to dictionary
        if (data != null)
        {
            message.Data = ConvertToDictionary(data);
        }

        return message;
    }

    private Dictionary<string, object> ConvertToDictionary(object data)
    {
        var dictionary = new Dictionary<string, object>();
        var properties = data.GetType().GetProperties();

        foreach (var prop in properties)
        {
            var value = prop.GetValue(data);
            if (value != null)
            {
                // Convert value to string or keep as-is if it's a primitive type
                if (value is string || value.GetType().IsPrimitive || value is DateTime || value is DateTimeOffset)
                {
                    dictionary[prop.Name] = value;
                }
                else
                {
                    dictionary[prop.Name] = JsonSerializer.Serialize(value);
                }
            }
        }

        return dictionary;
    }

    private async Task<ExpoPushResponse?> SendPushNotificationAsync(List<ExpoPushMessage> messages, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(ExpoPushApiUrl, messages, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Expo returns 400 (with details in the body) when the request contains a
                // malformed/non-Expo token or bad payload. Surface the body so the exact
                // reason/token is visible instead of an opaque "400 Bad Request".
                _logger.LogError(
                    "Expo push API returned {StatusCode}. Tokens: [{Tokens}]. Response: {Body}",
                    (int)response.StatusCode,
                    string.Join(", ", messages.Select(m => m.To)),
                    responseBody);
                return null;
            }

            return JsonSerializer.Deserialize<ExpoPushResponse>(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending push notification via Expo");
            return null;
        }
    }

    // Expo API Models
    private class ExpoPushMessage
    {
        [JsonPropertyName("to")]
        public string To { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("data")]
        public Dictionary<string, object>? Data { get; set; }

        [JsonPropertyName("sound")]
        public string? Sound { get; set; }

        [JsonPropertyName("priority")]
        public string? Priority { get; set; }

        [JsonPropertyName("channelId")]
        public string? ChannelId { get; set; }
    }

    private class ExpoPushResponse
    {
        [JsonPropertyName("data")]
        public List<ExpoPushResult>? Data { get; set; }
    }

    private class ExpoPushResult
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("details")]
        public object? Details { get; set; }
    }
}

