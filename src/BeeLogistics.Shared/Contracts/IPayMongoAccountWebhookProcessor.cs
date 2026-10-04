namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Processes PayMongo child-account lifecycle webhooks (identity verification passed/failed,
/// consumer activated/declined). Implemented in the API host so the Payment module, which owns the
/// webhook endpoint, stays decoupled from driver wallet logic — the same arrangement as
/// <see cref="IDisbursementWebhookProcessor"/>.
/// </summary>
public interface IPayMongoAccountWebhookProcessor
{
    /// <param name="accountId">The child account id (org_...) the event concerns.</param>
    /// <param name="eventType">PayMongo's event name, e.g. "consumer.activated".</param>
    /// <param name="activationStatus">
    /// The account's activation status when the event carries one; null otherwise.
    /// </param>
    /// <param name="failureReason">
    /// Why an identity check failed, when the event carries one — e.g. "Image quality check failed:
    /// blur detection". Actionable by the driver, so it must reach them rather than being logged.
    /// </param>
    Task ProcessAsync(string accountId, string eventType, string? activationStatus,
        string? failureReason = null, CancellationToken ct = default);
}
