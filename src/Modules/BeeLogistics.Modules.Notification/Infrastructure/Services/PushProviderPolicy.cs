using BeeLogistics.Modules.Notification.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace BeeLogistics.Modules.Notification.Infrastructure.Services;

/// <summary>
/// Resolves the active push vendor and expected token type from the "Push:Provider"
/// config key. Default (unset) is flag-based routing, which prefers native FCM/APNs.
/// </summary>
public class PushProviderPolicy : IPushProviderPolicy
{
    private readonly string _provider;

    public PushProviderPolicy(IConfiguration configuration)
    {
        var configured = configuration["Push:Provider"];
        _provider = configured?.Trim().ToLowerInvariant() switch
        {
            "expo" => "expo",
            "firebase" => "firebase",
            _ => "routing"
        };
    }

    public string ActiveProvider => _provider;

    public string ExpectedTokenType(string platform)
    {
        // When the backend is pinned to Expo, every platform should register Expo tokens.
        if (_provider == "expo")
            return PushTokenTypes.Expo;

        // Firebase or routing default -> native tokens (FCM on Android, APNs on iOS).
        return string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
            ? PushTokenTypes.Apns
            : PushTokenTypes.Fcm;
    }
}
