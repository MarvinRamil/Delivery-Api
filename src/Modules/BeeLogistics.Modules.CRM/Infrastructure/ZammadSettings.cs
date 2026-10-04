namespace BeeLogistics.Modules.CRM.Infrastructure;

/// <summary>
/// Configuration settings for the Zammad helpdesk integration.
/// Bound from the "Zammad" section in appsettings.json.
/// </summary>
public class ZammadSettings
{
    public const string SectionName = "Zammad";

    /// <summary>
    /// Base URL of the Zammad instance (e.g., http://100.105.242.96:9080).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Personal Access Token for Zammad API authentication.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Default Zammad group for customer tickets.
    /// </summary>
    /// <remarks>
    /// The group is the queue: it alone decides which agents can see a ticket and who is
    /// offered in its Owner dropdown. The organization Zammad derives from the customer is a
    /// customer-portal concept and grants agents nothing, so filing into a group no agent holds
    /// permission on produces tickets that sync cleanly and are visible to nobody. Give the
    /// Agent role full permission on a group before naming it here.
    /// </remarks>
    public string CustomerGroup { get; set; } = "Users";

    /// <summary>
    /// Default Zammad group for driver tickets. Same caveat as <see cref="CustomerGroup"/>.
    /// </summary>
    public string DriverGroup { get; set; } = "Users";

    /// <summary>
    /// Whether the Zammad integration is enabled.
    /// Set to false to disable syncing without removing configuration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// HMAC-SHA1 secret for verifying incoming webhook signatures (X-Hub-Signature).
    /// Must match the "HMAC SHA1 Signature Token" configured in Zammad's webhook.
    /// </summary>
    public string? WebhookSecret { get; set; }
}
