namespace BeeLogistics.Modules.Verification.Infrastructure;

public class DiditOptions
{
    public const string SectionName = "Didit";

    public bool Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "https://verification.didit.me";
    public string ApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string WorkflowId { get; set; } = string.Empty;
    /// <summary>
    /// Didit workflow for CUSTOMER identity verification. Distinct from the driver <see cref="WorkflowId"/>
    /// so the two flows can differ (required documents, checks). Falls back to <see cref="WorkflowId"/> when empty.
    /// </summary>
    public string CustomerWorkflowId { get; set; } = string.Empty;
    /// <summary>
    /// Where the hosted flow redirects when the user finishes. A brand-neutral sentinel that both the
    /// driver and customer apps intercept inside the WebView (it never has to match either app's OS scheme).
    /// </summary>
    public string CallbackUrl { get; set; } = "beeapp://kyc-callback";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Max allowed age of the X-Timestamp webhook header, for replay protection.</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;
}
