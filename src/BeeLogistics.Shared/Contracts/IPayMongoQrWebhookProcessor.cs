namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Processes PayMongo <c>qr.paid</c> webhooks — someone scanned a driver's BeeWallet QR and funded
/// their wallet. Implemented in the API host so the Payment module, which owns the webhook
/// endpoint, stays free of driver wallet logic.
/// </summary>
public interface IPayMongoQrWebhookProcessor
{
    /// <param name="creditAccountNumber">
    /// The wallet credited. The most direct identifier we have, since it is stored verbatim on the
    /// driver's wallet. Can be empty on P2M payments.
    /// </param>
    /// <param name="accountId">
    /// Child account id (org_...), when the payload carries one. The top-level <c>merchant_id</c>
    /// omits the <c>org_</c> prefix while <c>metadata.merchant_id</c> includes it, so callers should
    /// normalise before passing it here.
    /// </param>
    /// <param name="idempotencyKey">
    /// Unique per payment — the transfer id, NOT the QR id. A static QR keeps one id across every
    /// payment it ever receives, so keying on it would credit only the first.
    /// </param>
    /// <param name="referenceLabel">
    /// What the QR was issued for, when it says. A BeeWallet top-up QR carries none and credits the
    /// driver's wallet; a cashbond QR carries <c>cashbond-{transactionId:N}</c> and settles that
    /// transaction instead. A cashbond/premium QR credits the platform wallet rather than a driver's
    /// child wallet, so this label is the only link back to the driver.
    /// </param>
    Task ProcessAsync(
        string? creditAccountNumber,
        string? accountId,
        decimal amount,
        string idempotencyKey,
        string? referenceLabel = null,
        CancellationToken ct = default);
}
