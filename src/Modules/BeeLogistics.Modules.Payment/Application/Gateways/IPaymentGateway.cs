namespace BeeLogistics.Modules.Payment.Application.Gateways;

/// <summary>Hosted checkout: create a payment page, poll its status, force-expire it.</summary>
public interface ICollectionGateway
{
    Task<CheckoutSession> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default);
    Task<CheckoutSession?> GetCheckoutAsync(string providerPaymentId, CancellationToken ct = default);

    /// <summary>
    /// Best-effort provider-side expiry. Xendit expires invoices server-side
    /// (invoice_duration) so its implementation is a no-op; PayMongo checkout
    /// sessions have no duration and are expired explicitly by the expiry job.
    /// </summary>
    Task ExpireCheckoutAsync(string providerPaymentId, CancellationToken ct = default);
}

public interface IRefundGateway
{
    /// <summary>Returns null when the provider rejected the refund (already logged by the adapter).</summary>
    Task<GatewayRefund?> CreateRefundAsync(CreateGatewayRefundRequest request, CancellationToken ct = default);

    /// <summary>
    /// Polls the provider for a refund's current status, for resolving one stuck in RefundPending
    /// after an ambiguous CreateRefundAsync outcome (thrown exception - the local record never
    /// learned the true result). Returns null when the provider has no record of this refund id.
    /// </summary>
    Task<GatewayRefund?> GetRefundAsync(string providerRefundId, CancellationToken ct = default);
}

public interface IDisbursementGateway
{
    Task<GatewayDisbursement> CreateDisbursementAsync(CreateGatewayDisbursementRequest request, CancellationToken ct = default);
    Task<GatewayDisbursement?> GetDisbursementAsync(string providerDisbursementId, CancellationToken ct = default);

    /// <summary>
    /// Finds a disbursement by OUR reference id rather than the provider's.
    /// The recovery path for an ambiguous create: when the call timed out we hold a
    /// reference but no provider id, and this is the only way to learn whether the transfer
    /// actually exists before deciding to refund. Returns null when the provider has no
    /// record of it — which is the one case where refunding is safe.
    /// </summary>
    Task<GatewayDisbursement?> FindDisbursementByReferenceAsync(string referenceId, CancellationToken ct = default);

    /// <summary>
    /// Pays out against a scanned QR Ph code instead of a bank account.
    /// Adapters whose provider has no QR rail throw <see cref="NotSupportedException"/>;
    /// callers surface that as "QR withdrawal isn't available right now".
    /// </summary>
    Task<GatewayDisbursement> ExecuteQrDisbursementAsync(ExecuteGatewayQrDisbursementRequest request, CancellationToken ct = default);
}

/// <summary>Saved payment methods: provider customers + tokenized cards/e-wallets.</summary>
public interface IVaultGateway
{
    Task<GatewayCustomer> CreateCustomerAsync(CreateGatewayCustomerRequest request, CancellationToken ct = default);
    Task<GatewayCustomer?> GetCustomerAsync(string providerCustomerId, CancellationToken ct = default);
    Task<GatewayPaymentMethod> CreatePaymentMethodAsync(CreateGatewayPaymentMethodRequest request, CancellationToken ct = default);
    Task<GatewayPaymentMethod?> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default);
    Task DeletePaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default);
}

/// <summary>
/// A payment provider adapter. Consumers obtain instances through
/// <see cref="IPaymentGatewayFactory"/> — GetActive() for new records,
/// Get(record.Provider) for anything that already exists.
/// </summary>
public interface IPaymentGateway : ICollectionGateway, IRefundGateway, IDisbursementGateway, IVaultGateway
{
    /// <summary>Canonical lowercase provider name (see <see cref="PaymentProviders"/>); stored on domain records.</summary>
    string ProviderName { get; }
}
