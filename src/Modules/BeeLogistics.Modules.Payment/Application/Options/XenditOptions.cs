namespace BeeLogistics.Modules.Payment.Application.Options;

/// <summary>
/// Xendit gateway configuration ("Xendit" section). Secrets come from Vault
/// (overrides env at startup); env vars use the Xendit__ prefix.
/// </summary>
public sealed class XenditOptions
{
    public const string SectionName = "Xendit";

    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.xendit.co/";

    /// <summary>Shared callback token compared against the x-callback-token webhook header.</summary>
    public string? WebhookToken { get; set; }

    /// <summary>Base64 SubjectPublicKeyInfo RSA key for x-xendit-signature verification.</summary>
    public string? PublicKey { get; set; }

    /// <summary>When true (default), production rejects webhooks that fail all auth checks.</summary>
    public bool StrictWebhookValidation { get; set; } = true;

    public string[] AllowedSourceIps { get; set; } = [];

    public int TopUpInvoiceDurationSeconds { get; set; } = 86400;

    /// <summary>True when an API key is present; used to decide whether this gateway is usable.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
