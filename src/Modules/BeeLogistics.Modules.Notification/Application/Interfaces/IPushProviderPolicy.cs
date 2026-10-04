namespace BeeLogistics.Modules.Notification.Application.Interfaces;

/// <summary>
/// Single source of truth for the active push vendor and the token type clients
/// should register per platform. Driven by the "Push:Provider" config key
/// (Firebase | Expo | unset=routing). Used by the register handler and the
/// GET api/notifications/push-config endpoint so mobile apps stay in sync when the
/// backend changes vendor.
/// </summary>
public interface IPushProviderPolicy
{
    /// <summary>Effective provider: "firebase", "expo", or "routing" (accepts both).</summary>
    string ActiveProvider { get; }

    /// <summary>
    /// The token type the backend expects for the given platform ("ios"/"android"):
    /// "fcm", "apns", or "expo".
    /// </summary>
    string ExpectedTokenType(string platform);
}

/// <summary>Well-known device token type flags.</summary>
public static class PushTokenTypes
{
    public const string Fcm = "fcm";
    public const string Apns = "apns";
    public const string Expo = "expo";
}
