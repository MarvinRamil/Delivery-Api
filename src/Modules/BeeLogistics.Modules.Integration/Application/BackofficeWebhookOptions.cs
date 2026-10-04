namespace BeeLogistics.Modules.Integration.Application;

public class BackofficeWebhookOptions
{
    public const string SectionName = "BackofficeWebhooks";

    public bool Enabled { get; set; }
    /// <summary>Receiver endpoint on the back-office backend, e.g. http://localhost:5340/api/webhooks/bee</summary>
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Shared HMAC secret (Vault). The receiver verifies X-Bee-Signature with it.</summary>
    public string Secret { get; set; } = string.Empty;
}
