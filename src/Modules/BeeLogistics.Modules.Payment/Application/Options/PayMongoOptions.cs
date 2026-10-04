namespace BeeLogistics.Modules.Payment.Application.Options;

/// <summary>
/// PayMongo gateway configuration ("PayMongo" section). Secrets come from Vault
/// (overrides env at startup); env vars use the PayMongo__ prefix.
/// </summary>
public sealed class PayMongoOptions
{
    public const string SectionName = "PayMongo";

    /// <summary>Secret key (sk_test_... / sk_live_...) used as the Basic auth username.</summary>
    public string SecretKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.paymongo.com/";

    /// <summary>Per-webhook signing secret (whsk_...) for Paymongo-Signature HMAC verification.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>When true (default), production rejects webhooks that fail signature verification.</summary>
    public bool StrictWebhookValidation { get; set; } = true;

    /// <summary>Max allowed age of the webhook signature timestamp (replay protection).</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    /// <summary>Redirect URL after a successful hosted checkout (required by checkout sessions).</summary>
    public string CheckoutSuccessUrl { get; set; } = "";

    /// <summary>Redirect URL when the payer cancels a hosted checkout.</summary>
    public string CheckoutCancelUrl { get; set; } = "";

    /// <summary>Transfer rail for disbursements: "instapay" (instant, ≤ PHP 50k) or "pesonet".</summary>
    public string DefaultTransferProvider { get; set; } = "instapay";

    /// <summary>
    /// Merchant source account for outbound transfers (batch_transfers requires it).
    /// These identify the PayMongo wallet money is sent FROM.
    /// </summary>
    public string SourceAccountNumber { get; set; } = "";
    public string SourceAccountName { get; set; } = "";
    public string SourceAccountBic { get; set; } = "";

    /// <summary>
    /// Absolute URL PayMongo POSTs transfer status changes to (the existing
    /// POST /api/webhooks/paymongo handler). Optional: when empty the field is omitted
    /// and status is resolved by the reconciliation job instead.
    /// </summary>
    public string TransferCallbackUrl { get; set; } = "";

    /// <summary>True when disbursements can be issued (source account configured).</summary>
    public bool DisbursementsConfigured =>
        !string.IsNullOrWhiteSpace(SourceAccountNumber) &&
        !string.IsNullOrWhiteSpace(SourceAccountName) &&
        !string.IsNullOrWhiteSpace(SourceAccountBic);

    /// <summary>True when a secret key is present; used to decide whether this gateway is usable.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);
}
