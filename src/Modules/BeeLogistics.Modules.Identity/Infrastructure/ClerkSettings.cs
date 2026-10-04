namespace BeeLogistics.Modules.Identity.Infrastructure;

/// <summary>
/// Clerk configuration (bound from the "Clerk" config section).
/// When <see cref="Authority"/>/<see cref="Issuer"/> are empty the Clerk auth
/// scheme is NOT registered, so existing legacy-JWT behaviour is unchanged.
/// This lets us ship the integration additively and turn it on by config.
/// </summary>
public class ClerkSettings
{
    public const string SectionName = "Clerk";

    /// <summary>Clerk Frontend API URL, e.g. https://your-app.clerk.accounts.dev</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Token issuer (usually equals Authority).</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>JWKS endpoint. If empty, derived from Authority (/.well-known/jwks.json).</summary>
    public string JwksUrl { get; set; } = string.Empty;

    /// <summary>Optional expected audience. Empty = audience validation disabled.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Svix signing secret for verifying Clerk webhooks.</summary>
    public string WebhookSigningSecret { get; set; } = string.Empty;

    /// <summary>Clerk Backend API secret key (sk_...). Used later for sign-in tokens / user import. Server-side only.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>True when Clerk auth should be wired up (config present).</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority) || !string.IsNullOrWhiteSpace(Issuer);
}
