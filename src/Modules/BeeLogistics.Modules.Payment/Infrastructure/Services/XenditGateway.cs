using System.Net.Http.Json;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// Xendit adapter. Hosted checkout = Xendit invoices (v2/invoices), refunds =
/// POST refunds, disbursements = v2/payouts, vault = customers/payment_methods.
/// Provider-native statuses are normalized here and never leave the adapter.
/// </summary>
public class XenditGateway : IPaymentGateway
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<XenditGateway> _logger;

    public string ProviderName => PaymentProviders.Xendit;

    public XenditGateway(HttpClient httpClient, ILogger<XenditGateway> logger)
    {
        // Base address and Basic auth are configured on the typed HttpClient in
        // DependencyInjection.cs so the API key is never held by this class.
        _httpClient = httpClient;
        _logger = logger;
    }

    // --- Status normalization (provider-native strings stay inside this adapter) ---

    private static GatewayPaymentStatus MapPaymentStatus(string status) => status.ToUpperInvariant() switch
    {
        "PAID" or "SETTLED" => GatewayPaymentStatus.Paid,
        "EXPIRED" => GatewayPaymentStatus.Expired,
        "FAILED" => GatewayPaymentStatus.Failed,
        "PENDING" => GatewayPaymentStatus.Pending,
        _ => GatewayPaymentStatus.Unknown
    };

    private static GatewayRefundStatus MapRefundStatus(string status) => status.ToUpperInvariant() switch
    {
        "SUCCEEDED" => GatewayRefundStatus.Succeeded,
        "FAILED" => GatewayRefundStatus.Failed,
        "PENDING" => GatewayRefundStatus.Pending,
        _ => GatewayRefundStatus.Unknown
    };

    private static GatewayDisbursementStatus MapDisbursementStatus(string status) => status.ToUpperInvariant() switch
    {
        "COMPLETED" or "SUCCEEDED" => GatewayDisbursementStatus.Completed,
        "FAILED" => GatewayDisbursementStatus.Failed,
        // ACCEPTED, PENDING, LOCKED, REQUESTED... are all still in flight
        _ => GatewayDisbursementStatus.Pending
    };

    // --- Collections (hosted checkout via Xendit invoices) ---

    public async Task<CheckoutSession> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            external_id = request.ReferenceId,
            amount = request.Amount,
            payer_email = request.PayerEmail,
            description = request.Description,
            currency = request.Currency,
            invoice_duration = request.ExpirySeconds
        };

        _logger.LogInformation("Creating Xendit invoice for {ReferenceId} amount {Amount}", request.ReferenceId, request.Amount);

        var response = await _httpClient.PostAsJsonAsync("v2/invoices", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var rawStatus = result.GetProperty("status").GetString()!;

        return new CheckoutSession(
            ProviderPaymentId: result.GetProperty("id").GetString()!,
            ReferenceId: result.GetProperty("external_id").GetString()!,
            Status: MapPaymentStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            CheckoutUrl: result.GetProperty("invoice_url").GetString()!,
            PaidAt: null
        );
    }

    public async Task<CheckoutSession?> GetCheckoutAsync(string providerPaymentId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v2/invoices/{providerPaymentId}", ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        DateTime? paidAt = null;
        if (result.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind != JsonValueKind.Null)
        {
            paidAt = paidAtProp.GetDateTime();
        }

        // payment_request_id is the capture id refunds need; newer invoices carry it
        // top-level, older ones expose it via payments[0].
        string? captureId = null;
        if (result.TryGetProperty("payment_request_id", out var prIdProp) && prIdProp.ValueKind != JsonValueKind.Null)
            captureId = prIdProp.GetString();
        else if (result.TryGetProperty("payments", out var paymentsProp) && paymentsProp.ValueKind == JsonValueKind.Array && paymentsProp.GetArrayLength() > 0)
        {
            var first = paymentsProp[0];
            if (first.TryGetProperty("payment_request_id", out var prId) && prId.ValueKind != JsonValueKind.Null)
                captureId = prId.GetString();
        }

        var rawStatus = result.GetProperty("status").GetString()!;

        return new CheckoutSession(
            ProviderPaymentId: result.GetProperty("id").GetString()!,
            ReferenceId: result.GetProperty("external_id").GetString()!,
            Status: MapPaymentStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            CheckoutUrl: result.GetProperty("invoice_url").GetString()!,
            PaidAt: paidAt,
            ProviderCaptureId: captureId
        );
    }

    public Task ExpireCheckoutAsync(string providerPaymentId, CancellationToken ct = default)
    {
        // Xendit invoices expire server-side via invoice_duration; nothing to do here.
        return Task.CompletedTask;
    }

    // --- Refunds ---

    public async Task<GatewayRefund?> CreateRefundAsync(CreateGatewayRefundRequest request, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["payment_request_id"] = request.ProviderCaptureId,
            ["reason"] = request.Reason,
            ["currency"] = request.Currency
        };
        if (request.ReferenceId != null)
            payload["reference_id"] = request.ReferenceId;
        if (request.Amount.HasValue)
            payload["amount"] = request.Amount.Value;

        _logger.LogInformation(
            "Creating Xendit refund (ref {ReferenceId}), reason {Reason}, amount {Amount}",
            request.ReferenceId ?? "n/a", request.Reason, request.Amount);

        using var req = new HttpRequestMessage(HttpMethod.Post, "refunds");
        req.Content = JsonContent.Create(payload);
        // Idempotency: a retried request (network blip, resilience handler) must not
        // create a second refund. The reference id is stable per payment.
        if (!string.IsNullOrWhiteSpace(request.ReferenceId))
            req.Headers.TryAddWithoutValidation("idempotency-key", request.ReferenceId);

        var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Log status and provider error_code only; never the full body (may contain sensitive details)
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogWarning("Xendit refund request failed with status {StatusCode}, error_code {ErrorCode}",
                response.StatusCode, errorCode ?? "n/a");
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var rawStatus = result.GetProperty("status").GetString()!;

        return new GatewayRefund(
            ProviderRefundId: result.GetProperty("id").GetString()!,
            ProviderCaptureId: result.GetProperty("payment_request_id").GetString()!,
            Status: MapRefundStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            Currency: result.GetProperty("currency").GetString()!
        );
    }

    public async Task<GatewayRefund?> GetRefundAsync(string providerRefundId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"refunds/{providerRefundId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogWarning("Xendit GetRefund failed for {ProviderRefundId} with status {StatusCode}, error_code {ErrorCode}",
                providerRefundId, response.StatusCode, errorCode ?? "n/a");
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var rawStatus = result.GetProperty("status").GetString()!;

        return new GatewayRefund(
            ProviderRefundId: result.GetProperty("id").GetString()!,
            ProviderCaptureId: result.GetProperty("payment_request_id").GetString()!,
            Status: MapRefundStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            Currency: result.GetProperty("currency").GetString()!
        );
    }

    // --- Disbursements (Xendit v2 Payouts) ---

    public async Task<GatewayDisbursement> CreateDisbursementAsync(CreateGatewayDisbursementRequest request, CancellationToken ct = default)
    {
        // Xendit v2 Payouts API: reference_id, channel_code, channel_properties (account_holder_name, account_number)
        var channelCode = XenditBankCodeMap.Normalize(request.BankCode);

        var payload = new
        {
            reference_id = request.ReferenceId,
            channel_code = channelCode,
            channel_properties = new
            {
                account_holder_name = request.AccountHolderName,
                account_number = request.AccountNumber
            },
            amount = request.Amount,
            description = request.Description,
            currency = request.Currency
        };

        _logger.LogInformation(
            "Creating Xendit payout for {ReferenceId}, amount {Amount} to {ChannelCode}/{AccountNumber}",
            request.ReferenceId, request.Amount, channelCode, MaskAccountNumber(request.AccountNumber));

        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/payouts");
        req.Content = JsonContent.Create(payload);
        var idempotencyKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? request.IdempotencyKey.Trim()
            : request.ReferenceId;
        req.Headers.TryAddWithoutValidation("Idempotency-key", idempotencyKey);

        var response = await _httpClient.SendAsync(req, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Log status and provider error_code only; never the full body
            // (may contain account holder names / account numbers)
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogError(
                "[XENDIT] Payout failed - Status: {StatusCode}, ErrorCode: {ErrorCode}",
                response.StatusCode, errorCode ?? "n/a");
            throw new PaymentGatewayException(ProviderName, (int)response.StatusCode, errorCode,
                $"Xendit payout failed with status {(int)response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        string? failureCode = null;
        if (result.TryGetProperty("failure_code", out var fc) && fc.ValueKind != JsonValueKind.Null)
            failureCode = fc.GetString();

        var referenceId = result.TryGetProperty("reference_id", out var refProp) ? refProp.GetString()! : request.ReferenceId;
        var accountHolderName = result.TryGetProperty("channel_properties", out var cp) && cp.TryGetProperty("account_holder_name", out var ahn)
            ? ahn.GetString()!
            : request.AccountHolderName;
        var rawStatus = result.GetProperty("status").GetString()!;

        return new GatewayDisbursement(
            ProviderDisbursementId: result.GetProperty("id").GetString()!,
            ReferenceId: referenceId,
            Status: MapDisbursementStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            BankCode: result.GetProperty("channel_code").GetString()!,
            AccountHolderName: accountHolderName,
            FailureCode: failureCode
        );
    }

    /// <summary>
    /// Not supported by the legacy Xendit disbursement API. Throws rather than returning null
    /// so an ambiguous Xendit payout is never mistaken for "no transfer exists" and refunded.
    /// </summary>
    public Task<GatewayDisbursement?> FindDisbursementByReferenceAsync(string referenceId, CancellationToken ct = default)
        => throw new NotSupportedException("Xendit does not support looking up a payout by reference id.");

    /// <summary>Xendit has no QR Ph payout rail; callers fall back to a bank transfer.</summary>
    public Task<GatewayDisbursement> ExecuteQrDisbursementAsync(ExecuteGatewayQrDisbursementRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Xendit does not support QR Ph payouts.");

    public async Task<GatewayDisbursement?> GetDisbursementAsync(string providerDisbursementId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"disbursements/{providerDisbursementId}", ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        string? failureCode = null;
        if (result.TryGetProperty("failure_code", out var fc) && fc.ValueKind != JsonValueKind.Null)
            failureCode = fc.GetString();

        var rawStatus = result.GetProperty("status").GetString()!;

        return new GatewayDisbursement(
            ProviderDisbursementId: result.GetProperty("id").GetString()!,
            ReferenceId: result.GetProperty("external_id").GetString()!,
            Status: MapDisbursementStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: result.GetProperty("amount").GetDecimal(),
            BankCode: result.GetProperty("bank_code").GetString()!,
            AccountHolderName: result.GetProperty("account_holder_name").GetString()!,
            FailureCode: failureCode
        );
    }

    // --- Vault (Xendit customers and payment methods) ---

    public async Task<GatewayCustomer> CreateCustomerAsync(CreateGatewayCustomerRequest request, CancellationToken ct = default)
    {
        // PII: never log the payer email; the reference id is enough to correlate
        _logger.LogInformation(
            "[XENDIT] [CUSTOMER] [CREATE] Starting - ReferenceId: {ReferenceId}",
            request.ReferenceId);

        var payload = new Dictionary<string, object?>
        {
            ["reference_id"] = request.ReferenceId,
            ["email"] = request.Email,
            ["given_names"] = request.GivenNames
        };

        if (!string.IsNullOrEmpty(request.Surname))
            payload["surname"] = request.Surname;

        if (!string.IsNullOrEmpty(request.MobileNumber))
            payload["mobile_number"] = request.MobileNumber;

        try
        {
            var response = await _httpClient.PostAsJsonAsync("customers", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var customer = ParseCustomer(result);

            _logger.LogInformation(
                "[XENDIT] [CUSTOMER] [CREATE] Success - ReferenceId: {ReferenceId}, CustomerId: {CustomerId}",
                request.ReferenceId, customer.Id);

            return customer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[XENDIT] [CUSTOMER] [CREATE] Error - ReferenceId: {ReferenceId}",
                request.ReferenceId);
            throw;
        }
    }

    public async Task<GatewayCustomer?> GetCustomerAsync(string providerCustomerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"customers/{providerCustomerId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[XENDIT] [CUSTOMER] [GET] Not Found - CustomerId: {CustomerId}, StatusCode: {StatusCode}",
                    providerCustomerId, response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return ParseCustomer(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[XENDIT] [CUSTOMER] [GET] Error - CustomerId: {CustomerId}",
                providerCustomerId);
            throw;
        }
    }

    public async Task<GatewayPaymentMethod> CreatePaymentMethodAsync(CreateGatewayPaymentMethodRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[XENDIT] [PAYMENT_METHOD] [CREATE] Starting - CustomerId: {CustomerId}, Type: {Type}",
            request.CustomerId, request.Type);

        var payload = new Dictionary<string, object?>
        {
            ["customer_id"] = request.CustomerId,
            ["type"] = request.Type
        };

        if (request.Type == "CARD" && !string.IsNullOrEmpty(request.TokenOrClientKey))
        {
            payload["card"] = new Dictionary<string, string>
            {
                ["token"] = request.TokenOrClientKey
            };
        }
        else if (request.Type == "E_WALLET" && !string.IsNullOrEmpty(request.EWalletAccountId))
        {
            payload["ewallet"] = new Dictionary<string, string>
            {
                ["account_id"] = request.EWalletAccountId
            };
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync("payment_methods", payload, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var paymentMethod = ParsePaymentMethod(result);

            _logger.LogInformation(
                "[XENDIT] [PAYMENT_METHOD] [CREATE] Success - PaymentMethodId: {PaymentMethodId}, CustomerId: {CustomerId}, Type: {Type}, Status: {Status}",
                paymentMethod.Id, request.CustomerId, paymentMethod.Type, paymentMethod.Status);

            return paymentMethod;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[XENDIT] [PAYMENT_METHOD] [CREATE] Error - CustomerId: {CustomerId}, Type: {Type}",
                request.CustomerId, request.Type);
            throw;
        }
    }

    public async Task<GatewayPaymentMethod?> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"payment_methods/{providerPaymentMethodId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[XENDIT] [PAYMENT_METHOD] [GET] Not Found - PaymentMethodId: {PaymentMethodId}, StatusCode: {StatusCode}",
                    providerPaymentMethodId, response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return ParsePaymentMethod(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[XENDIT] [PAYMENT_METHOD] [GET] Error - PaymentMethodId: {PaymentMethodId}",
                providerPaymentMethodId);
            throw;
        }
    }

    public async Task DeletePaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"payment_methods/{providerPaymentMethodId}", ct);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "[XENDIT] [PAYMENT_METHOD] [DELETE] Success - PaymentMethodId: {PaymentMethodId}",
                providerPaymentMethodId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[XENDIT] [PAYMENT_METHOD] [DELETE] Error - PaymentMethodId: {PaymentMethodId}",
                providerPaymentMethodId);
            throw;
        }
    }

    // --- Parsing helpers ---

    private static GatewayCustomer ParseCustomer(JsonElement result) => new(
        Id: result.GetProperty("id").GetString()!,
        ReferenceId: result.GetProperty("reference_id").GetString()!,
        Email: result.GetProperty("email").GetString()!,
        GivenNames: result.GetProperty("given_names").GetString()!,
        Surname: result.TryGetProperty("surname", out var surname) && surname.ValueKind != JsonValueKind.Null ? surname.GetString() : null,
        MobileNumber: result.TryGetProperty("mobile_number", out var mobile) && mobile.ValueKind != JsonValueKind.Null ? mobile.GetString() : null
    );

    private static GatewayPaymentMethod ParsePaymentMethod(JsonElement result)
    {
        GatewayCardDetails? card = null;
        if (result.TryGetProperty("card", out var cardProp) && cardProp.ValueKind == JsonValueKind.Object)
        {
            card = new GatewayCardDetails(
                Last4: cardProp.GetProperty("last4").GetString()!,
                Brand: cardProp.TryGetProperty("brand", out var brand) && brand.ValueKind != JsonValueKind.Null ? brand.GetString() : null,
                ExpiryMonth: cardProp.TryGetProperty("expiry_month", out var expMonth) && expMonth.ValueKind != JsonValueKind.Null ? expMonth.GetInt32() : null,
                ExpiryYear: cardProp.TryGetProperty("expiry_year", out var expYear) && expYear.ValueKind != JsonValueKind.Null ? expYear.GetInt32() : null,
                CardholderName: cardProp.TryGetProperty("cardholder_name", out var name) && name.ValueKind != JsonValueKind.Null ? name.GetString() : null
            );
        }

        GatewayEWalletDetails? eWallet = null;
        if (result.TryGetProperty("ewallet", out var ewalletProp) && ewalletProp.ValueKind == JsonValueKind.Object)
        {
            eWallet = new GatewayEWalletDetails(
                ChannelCode: ewalletProp.GetProperty("channel_code").GetString()!,
                AccountId: ewalletProp.TryGetProperty("account_id", out var accId) && accId.ValueKind != JsonValueKind.Null ? accId.GetString() : null
            );
        }

        return new GatewayPaymentMethod(
            Id: result.GetProperty("id").GetString()!,
            CustomerId: result.GetProperty("customer_id").GetString()!,
            Type: result.GetProperty("type").GetString()!,
            Status: result.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null ? status.GetString() : null,
            Card: card,
            EWallet: eWallet
        );
    }

    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var errorBody = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (errorBody.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.String)
                return ec.GetString();
        }
        catch (JsonException) { /* non-JSON error body */ }
        return null;
    }

    /// <summary>Masks all but the last 4 characters of an account number for logging.</summary>
    private static string MaskAccountNumber(string? accountNumber)
    {
        if (string.IsNullOrEmpty(accountNumber))
            return "n/a";
        return accountNumber.Length <= 4
            ? "****"
            : $"****{accountNumber[^4..]}";
    }
}
