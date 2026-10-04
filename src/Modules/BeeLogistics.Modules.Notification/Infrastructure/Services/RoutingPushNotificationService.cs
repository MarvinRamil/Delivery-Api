using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Flag-based push router. Selects the delivery provider per device token from the
/// explicit registration flag (<see cref="DeviceToken.TokenType"/>), never by guessing:
///   fcm | apns          -> Firebase
///   expo                -> Expo
///   null / unknown      -> "ExponentPushToken[" prefix -> Expo (safety), otherwise SKIP.
/// Tokens that can't be routed are skipped and logged (never defaulted to a vendor), so a
/// vendor switch is self-healing once clients re-register with a proper flag.
/// </summary>
public class RoutingPushNotificationService : IPushNotificationService
{
    private readonly FirebaseNotificationService _firebase;
    private readonly ExpoPushNotificationService _expo;
    private readonly IDeviceTokenRepository _deviceTokenRepository;
    private readonly ILogger<RoutingPushNotificationService> _logger;

    private enum Provider { Firebase, Expo, Skip }

    public RoutingPushNotificationService(
        FirebaseNotificationService firebase,
        ExpoPushNotificationService expo,
        IDeviceTokenRepository deviceTokenRepository,
        ILogger<RoutingPushNotificationService> logger)
    {
        _firebase = firebase;
        _expo = expo;
        _deviceTokenRepository = deviceTokenRepository;
        _logger = logger;
    }

    public async Task<bool> SendToDeviceAsync(string deviceToken, string title, string body, object? data = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
            return false;

        var record = await _deviceTokenRepository.GetByTokenAsync(deviceToken, ct);
        switch (ResolveProvider(record?.TokenType, deviceToken))
        {
            case Provider.Firebase:
                return await _firebase.SendToDeviceAsync(deviceToken, title, body, data, ct);
            case Provider.Expo:
                return await _expo.SendToDeviceAsync(deviceToken, title, body, data, ct);
            default:
                _logger.LogWarning("Skipping push: unroutable token (no provider flag and not an Expo token).");
                return false;
        }
    }

    public async Task<int> SendToMultipleDevicesAsync(IEnumerable<string> deviceTokens, string title, string body, object? data = null, CancellationToken ct = default)
    {
        var tokens = deviceTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        if (tokens.Count == 0)
            return 0;

        var records = await _deviceTokenRepository.GetByTokensAsync(tokens, ct);
        var typeByToken = records
            .GroupBy(r => r.Token)
            .ToDictionary(g => g.Key, g => g.First().TokenType);

        return await RouteAndSendAsync(
            tokens.Select(t => (Token: t, TokenType: typeByToken.GetValueOrDefault(t))),
            title, body, data, ct);
    }

    public async Task<int> SendToUserAsync(string userId, string appType, string title, string body, object? data = null, CancellationToken ct = default)
    {
        var deviceTokens = await _deviceTokenRepository.GetByUserIdAsync(userId, appType, ct);
        if (deviceTokens.Count == 0)
        {
            _logger.LogDebug("No device tokens found for user {UserId} with app type {AppType}", userId, appType);
            return 0;
        }

        // Preserve prior behavior: bump LastUsedAt for the user's tokens.
        foreach (var deviceToken in deviceTokens)
        {
            deviceToken.LastUsedAt = DateTime.UtcNow;
            await _deviceTokenRepository.UpdateAsync(deviceToken, ct);
        }
        await _deviceTokenRepository.SaveChangesAsync(ct);

        return await RouteAndSendAsync(
            deviceTokens.Select(dt => (dt.Token, dt.TokenType)),
            title, body, data, ct);
    }

    private async Task<int> RouteAndSendAsync(
        IEnumerable<(string Token, string? TokenType)> tokens,
        string title, string body, object? data, CancellationToken ct)
    {
        var firebaseTokens = new List<string>();
        var expoTokens = new List<string>();
        var skipped = 0;

        foreach (var (token, tokenType) in tokens)
        {
            switch (ResolveProvider(tokenType, token))
            {
                case Provider.Firebase: firebaseTokens.Add(token); break;
                case Provider.Expo: expoTokens.Add(token); break;
                default: skipped++; break;
            }
        }

        if (skipped > 0)
            _logger.LogWarning("Push routing skipped {Skipped} unroutable token(s) (no provider flag and not Expo).", skipped);

        var sent = 0;
        if (firebaseTokens.Count > 0)
            sent += await _firebase.SendToMultipleDevicesAsync(firebaseTokens, title, body, data, ct);
        if (expoTokens.Count > 0)
            sent += await _expo.SendToMultipleDevicesAsync(expoTokens, title, body, data, ct);

        return sent;
    }

    private static Provider ResolveProvider(string? tokenType, string token)
    {
        if (!string.IsNullOrWhiteSpace(tokenType))
        {
            switch (tokenType.Trim().ToLowerInvariant())
            {
                case PushTokenTypes.Fcm:
                case PushTokenTypes.Apns:
                    return Provider.Firebase;
                case PushTokenTypes.Expo:
                    return Provider.Expo;
            }
        }

        // Unflagged / unknown flag: only route when the token is recognizably Expo,
        // otherwise skip rather than guess a vendor.
        return IsExpoToken(token) ? Provider.Expo : Provider.Skip;
    }

    private static bool IsExpoToken(string token) =>
        token.StartsWith("ExponentPushToken[", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("ExpoPushToken[", StringComparison.OrdinalIgnoreCase);
}
