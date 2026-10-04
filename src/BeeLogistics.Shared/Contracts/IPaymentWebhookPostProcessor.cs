namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Optional post-processors run after a payment (checkout) webhook is received
/// (e.g. driver top-up crediting). Implementations are registered in the API host
/// so payment and driver logic stay decoupled.
/// </summary>
public interface IPaymentWebhookPostProcessor
{
    /// <param name="provider">Canonical provider name: "xendit" | "paymongo".</param>
    /// <param name="providerPaymentId">Provider checkout id: Xendit invoice id / PayMongo checkout session id.</param>
    /// <param name="status">Provider-native status string; implementations treat PAID/SETTLED as paid.</param>
    /// <param name="currency">
    /// ISO code the provider actually charged in, when it reports one. Null means the provider
    /// did not tell us — implementations must decide whether that is acceptable rather than
    /// assuming it matched.
    /// </param>
    Task ProcessAsync(string provider, string providerPaymentId, string status, DateTime? paidAt, decimal? paidAmount, string? externalId, string? currency = null, CancellationToken ct = default);

    /// <summary>
    /// A payment attempt the provider declined.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ProcessAsync"/> because the provider reports it against a
    /// different resource: the failed <b>payment</b>, whose id matches no checkout we stored, so
    /// <paramref name="externalId"/> is the only reliable join back to our record.
    ///
    /// Implementations must not treat this as terminal. A decline does not necessarily end the
    /// checkout session, and the customer may retry on the same link.
    ///
    /// Default no-op so implementations that only care about successful payments are unaffected.
    /// </remarks>
    /// <param name="providerPaymentId">Provider id of the failed payment attempt, not the checkout.</param>
    /// <param name="externalId">Our reference number, echoed back by the provider.</param>
    Task ProcessFailedAsync(string provider, string providerPaymentId, string? externalId, string? reason, DateTime? failedAt, CancellationToken ct = default)
        => Task.CompletedTask;
}
