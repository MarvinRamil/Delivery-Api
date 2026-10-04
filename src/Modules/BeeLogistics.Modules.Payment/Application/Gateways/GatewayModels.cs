namespace BeeLogistics.Modules.Payment.Application.Gateways;

/// <summary>
/// Canonical provider names. These exact strings are stored in the Provider
/// columns (Payments, SavedPaymentMethods, DriverTopUps, WithdrawalRequests),
/// in PaymentWebhookEvent.Provider, and accepted by Payments:ActiveGateway.
/// </summary>
public static class PaymentProviders
{
    public const string Xendit = "xendit";
    public const string PayMongo = "paymongo";

    /// <summary>Normalizes a configured/stored provider name to its canonical form, or null when unknown.</summary>
    public static string? Normalize(string? providerName)
    {
        if (string.Equals(providerName, Xendit, StringComparison.OrdinalIgnoreCase)) return Xendit;
        if (string.Equals(providerName, PayMongo, StringComparison.OrdinalIgnoreCase)) return PayMongo;
        return null;
    }
}

// Provider-native statuses never leave the adapters: each adapter maps its raw
// strings ("SETTLED", "checkout_session.payment.paid", ...) to these enums and
// keeps the original in RawStatus for logs/audit only.
public enum GatewayPaymentStatus { Pending, Paid, Expired, Failed, Unknown }
public enum GatewayRefundStatus { Pending, Succeeded, Failed, Unknown }
public enum GatewayDisbursementStatus { Pending, Completed, Failed, Unknown }

// --- Collections (hosted checkout) ---

public record CreateCheckoutRequest(
    string ReferenceId,            // our PaymentNumber / DRVTOPUP-... external id
    decimal Amount,                // PHP major units; adapters convert as needed (PayMongo uses centavos)
    string PayerEmail,
    string Description,
    string Currency = "PHP",
    int ExpirySeconds = 86400,
    string? SuccessUrl = null,     // PayMongo checkout sessions require redirect URLs; falls back to PayMongoOptions
    string? CancelUrl = null);

public record CheckoutSession(
    string ProviderPaymentId,      // Xendit invoice id | PayMongo checkout session id (cs_...)
    string ReferenceId,
    GatewayPaymentStatus Status,
    string RawStatus,
    decimal Amount,
    string CheckoutUrl,
    DateTime? PaidAt,
    string? ProviderCaptureId = null); // the id refunds need: Xendit payment_request_id | PayMongo payment id (pay_...)

// --- Refunds ---

public record CreateGatewayRefundRequest(
    string ProviderCaptureId,
    string Reason,
    decimal? Amount = null,
    string Currency = "PHP",
    string? ReferenceId = null);

public record GatewayRefund(
    string ProviderRefundId,
    string ProviderCaptureId,
    GatewayRefundStatus Status,
    string RawStatus,
    decimal Amount,
    string Currency,
    string? FailureCode = null);

// --- Disbursements ---

public record CreateGatewayDisbursementRequest(
    string ReferenceId,
    decimal Amount,
    string BankCode,               // friendly name/code (BPI, BDO, GCASH...); each adapter normalizes at call time
    string AccountHolderName,
    string AccountNumber,
    string Description,
    string Currency = "PHP",
    string? IdempotencyKey = null);

/// <summary>
/// A QR Ph payout: the payee's scanned QR replaces bank code + account number.
/// The QR string is passed to the provider verbatim; we never parse it ourselves.
/// </summary>
public record ExecuteGatewayQrDisbursementRequest(
    string ReferenceId,
    decimal Amount,
    string QrString,
    string Currency = "PHP",
    string? IdempotencyKey = null);

public record GatewayDisbursement(
    string ProviderDisbursementId,
    string ReferenceId,
    GatewayDisbursementStatus Status,
    string RawStatus,
    decimal Amount,
    string BankCode,
    string AccountHolderName,
    string? FailureCode = null);

// --- Vault (customers + tokenized payment methods) ---

public record CreateGatewayCustomerRequest(
    string ReferenceId,            // our customer id (Guid as string)
    string Email,
    string GivenNames,
    string? Surname = null,
    string? MobileNumber = null);

public record GatewayCustomer(
    string Id,
    string ReferenceId,
    string Email,
    string GivenNames,
    string? Surname = null,
    string? MobileNumber = null);

public record CreateGatewayPaymentMethodRequest(
    string CustomerId,             // provider customer id
    string Type,                   // CARD, E_WALLET
    string? TokenOrClientKey = null,   // opaque client-side token (Xendit.js card token / PayMongo pm_... id)
    string? EWalletAccountId = null);

public record GatewayPaymentMethod(
    string Id,
    string CustomerId,
    string Type,
    string? Status = null,
    GatewayCardDetails? Card = null,
    GatewayEWalletDetails? EWallet = null);

public record GatewayCardDetails(
    string Last4,
    string? Brand = null,
    int? ExpiryMonth = null,
    int? ExpiryYear = null,
    string? CardholderName = null);

public record GatewayEWalletDetails(
    string ChannelCode,
    string? AccountId = null);

/// <summary>
/// Thrown by adapters when a create operation gets a non-2xx response.
/// Carries status + provider error code only — never response bodies or secrets.
/// </summary>
/// <summary>
/// The gateway is wired up for collections but not for payouts (e.g. PayMongo without
/// SourceAccount*). Distinct from <see cref="PaymentGatewayException"/> because nothing
/// left the process — retrying cannot help, and the driver must not be told "try again".
/// </summary>
public class PayoutsNotConfiguredException : InvalidOperationException
{
    public string Provider { get; }

    public PayoutsNotConfiguredException(string provider, string message) : base(message)
        => Provider = provider;
}

public class PaymentGatewayException : Exception
{
    public string Provider { get; }
    public int StatusCode { get; }
    public string? ProviderErrorCode { get; }

    public PaymentGatewayException(string provider, int statusCode, string? providerErrorCode, string message)
        : base(message)
    {
        Provider = provider;
        StatusCode = statusCode;
        ProviderErrorCode = providerErrorCode;
    }
}
