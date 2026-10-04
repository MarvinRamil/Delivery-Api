namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Processes disbursement/payout webhooks (Xendit payout.succeeded/payout.failed,
/// PayMongo transfer.*). Implementations are registered in the API host so payment
/// and driver wallet logic stay decoupled.
/// </summary>
public interface IDisbursementWebhookProcessor
{
    /// <param name="provider">Canonical provider name: "xendit" | "paymongo".</param>
    Task ProcessAsync(string provider, string disbursementId, string eventType, string status, string? failureReason, CancellationToken ct = default);
}
