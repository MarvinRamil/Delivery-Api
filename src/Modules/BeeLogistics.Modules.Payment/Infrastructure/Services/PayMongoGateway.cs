using System.Net.Http.Json;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Banks;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// PayMongo adapter. Hosted checkout = Checkout Sessions (v1/checkout_sessions),
/// refunds = v1/refunds, disbursements = v2/batch_transfers (InstaPay/PesoNet),
/// vault = v1/customers + v1/payment_methods.
/// Amounts are integer centavos on the wire; this adapter converts both directions.
/// Provider-native statuses are normalized here and never leave the adapter.
/// </summary>
public class PayMongoGateway : IPaymentGateway
{
    private readonly HttpClient _httpClient;
    private readonly PayMongoOptions _options;
    private readonly ILogger<PayMongoGateway> _logger;

    public string ProviderName => PaymentProviders.PayMongo;

    public PayMongoGateway(HttpClient httpClient, IOptions<PayMongoOptions> options, ILogger<PayMongoGateway> logger)
    {
        // Base address and Basic auth are configured on the typed HttpClient in
        // DependencyInjection.cs so the secret key is never held by this class.
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    // --- Amount conversion (highest-risk mapping: PayMongo uses integer centavos) ---

    public static long ToCentavos(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromCentavos(long centavos) => centavos / 100m;

    // --- Collections (hosted checkout via PayMongo Checkout Sessions) ---

    public async Task<CheckoutSession> CreateCheckoutAsync(CreateCheckoutRequest request, CancellationToken ct = default)
    {
        var successUrl = request.SuccessUrl ?? _options.CheckoutSuccessUrl;
        var cancelUrl = request.CancelUrl ?? _options.CheckoutCancelUrl;

        var payload = new
        {
            data = new
            {
                attributes = new
                {
                    line_items = new[]
                    {
                        new
                        {
                            name = string.IsNullOrWhiteSpace(request.Description) ? request.ReferenceId : request.Description,
                            amount = ToCentavos(request.Amount),
                            currency = request.Currency,
                            quantity = 1
                        }
                    },
                    payment_method_types = new[] { "card", "gcash", "paymaya", "qrph" },
                    reference_number = request.ReferenceId,
                    description = request.Description,
                    success_url = string.IsNullOrWhiteSpace(successUrl) ? null : successUrl,
                    cancel_url = string.IsNullOrWhiteSpace(cancelUrl) ? null : cancelUrl,
                    send_email_receipt = false
                }
            }
        };

        _logger.LogInformation("Creating PayMongo checkout session for {ReferenceId} amount {Amount}", request.ReferenceId, request.Amount);

        var response = await _httpClient.PostAsJsonAsync("v1/checkout_sessions", payload,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogError("[PAYMONGO] Checkout session create failed - Status: {StatusCode}, ErrorCode: {ErrorCode}",
                response.StatusCode, errorCode ?? "n/a");
            throw new PaymentGatewayException(ProviderName, (int)response.StatusCode, errorCode,
                $"PayMongo checkout session create failed with status {(int)response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = result.GetProperty("data");
        var attributes = data.GetProperty("attributes");

        return new CheckoutSession(
            ProviderPaymentId: data.GetProperty("id").GetString()!,
            ReferenceId: request.ReferenceId,
            Status: GatewayPaymentStatus.Pending,
            RawStatus: TryGetString(attributes, "status") ?? "active",
            Amount: request.Amount,
            CheckoutUrl: attributes.GetProperty("checkout_url").GetString()!,
            PaidAt: null
        );
    }

    public async Task<CheckoutSession?> GetCheckoutAsync(string providerPaymentId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v1/checkout_sessions/{providerPaymentId}", ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = result.GetProperty("data");
        var attributes = data.GetProperty("attributes");

        var rawStatus = TryGetString(attributes, "status") ?? "active";
        var status = GatewayPaymentStatus.Pending;
        string? captureId = null;
        DateTime? paidAt = null;
        decimal amount = 0m;

        // A session is paid when it carries at least one paid payment; the payment id
        // (pay_...) is the capture id refunds are issued against.
        if (attributes.TryGetProperty("payments", out var payments) &&
            payments.ValueKind == JsonValueKind.Array &&
            payments.GetArrayLength() > 0)
        {
            var payment = payments[0];
            var paymentAttributes = payment.GetProperty("attributes");
            var paymentStatus = TryGetString(paymentAttributes, "status");

            if (string.Equals(paymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                status = GatewayPaymentStatus.Paid;
                captureId = TryGetString(payment, "id");
                amount = FromCentavos(paymentAttributes.GetProperty("amount").GetInt64());

                if (paymentAttributes.TryGetProperty("paid_at", out var paidAtProp) && paidAtProp.ValueKind == JsonValueKind.Number)
                    paidAt = DateTimeOffset.FromUnixTimeSeconds(paidAtProp.GetInt64()).UtcDateTime;
            }
        }

        if (status == GatewayPaymentStatus.Pending && string.Equals(rawStatus, "expired", StringComparison.OrdinalIgnoreCase))
            status = GatewayPaymentStatus.Expired;

        if (amount == 0m &&
            attributes.TryGetProperty("line_items", out var lineItems) &&
            lineItems.ValueKind == JsonValueKind.Array &&
            lineItems.GetArrayLength() > 0 &&
            lineItems[0].TryGetProperty("amount", out var lineAmount))
        {
            amount = FromCentavos(lineAmount.GetInt64());
        }

        return new CheckoutSession(
            ProviderPaymentId: data.GetProperty("id").GetString()!,
            ReferenceId: TryGetString(attributes, "reference_number") ?? "",
            Status: status,
            RawStatus: status == GatewayPaymentStatus.Paid ? "paid" : rawStatus,
            Amount: amount,
            CheckoutUrl: TryGetString(attributes, "checkout_url") ?? "",
            PaidAt: paidAt,
            ProviderCaptureId: captureId
        );
    }

    public async Task ExpireCheckoutAsync(string providerPaymentId, CancellationToken ct = default)
    {
        // Checkout sessions have no server-side duration; the expiry job calls this
        // when the local ExpiresAt passes. Best-effort: an already paid/expired
        // session returns an error we only log.
        var response = await _httpClient.PostAsync($"v1/checkout_sessions/{providerPaymentId}/expire", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogWarning("[PAYMONGO] Expire checkout {ProviderPaymentId} returned {StatusCode} ({ErrorCode})",
                providerPaymentId, response.StatusCode, errorCode ?? "n/a");
        }
    }

    // --- Refunds ---

    public async Task<GatewayRefund?> CreateRefundAsync(CreateGatewayRefundRequest request, CancellationToken ct = default)
    {
        if (request.Amount is null)
            throw new ArgumentException("PayMongo refunds require an explicit amount", nameof(request));

        // PayMongo accepts a fixed reason vocabulary; our normalized reasons map onto
        // it and anything else lands in "others" with the original text in notes.
        var (reason, notes) = MapRefundReason(request.Reason);

        var payload = new
        {
            data = new
            {
                attributes = new
                {
                    amount = ToCentavos(request.Amount.Value),
                    payment_id = request.ProviderCaptureId,
                    reason,
                    notes,
                    metadata = request.ReferenceId != null ? new { reference_id = request.ReferenceId } : null
                }
            }
        };

        _logger.LogInformation(
            "Creating PayMongo refund (ref {ReferenceId}), reason {Reason}, amount {Amount}",
            request.ReferenceId ?? "n/a", reason, request.Amount);

        // NOTE: PayMongo has no idempotency-key header for refunds. Duplicate protection
        // relies on the RefundPending state guard in RefundPaymentCommandHandler and on
        // retries being disabled for unsafe HTTP methods on this client.
        var response = await _httpClient.PostAsJsonAsync("v1/refunds", payload,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Log status and provider error code only; never the full body
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogWarning("PayMongo refund request failed with status {StatusCode}, error_code {ErrorCode}",
                response.StatusCode, errorCode ?? "n/a");
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = result.GetProperty("data");
        var attributes = data.GetProperty("attributes");
        var rawStatus = TryGetString(attributes, "status") ?? "pending";

        return new GatewayRefund(
            ProviderRefundId: data.GetProperty("id").GetString()!,
            ProviderCaptureId: TryGetString(attributes, "payment_id") ?? request.ProviderCaptureId,
            Status: MapRefundStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: FromCentavos(attributes.GetProperty("amount").GetInt64()),
            Currency: TryGetString(attributes, "currency") ?? request.Currency
        );
    }

    public async Task<GatewayRefund?> GetRefundAsync(string providerRefundId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v1/refunds/{providerRefundId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogWarning("PayMongo GetRefund failed for {ProviderRefundId} with status {StatusCode}, error_code {ErrorCode}",
                providerRefundId, response.StatusCode, errorCode ?? "n/a");
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = result.GetProperty("data");
        var attributes = data.GetProperty("attributes");
        var rawStatus = TryGetString(attributes, "status") ?? "pending";

        return new GatewayRefund(
            ProviderRefundId: data.GetProperty("id").GetString()!,
            ProviderCaptureId: TryGetString(attributes, "payment_id") ?? string.Empty,
            Status: MapRefundStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: FromCentavos(attributes.GetProperty("amount").GetInt64()),
            Currency: TryGetString(attributes, "currency") ?? "PHP"
        );
    }

    // --- Disbursements (PayMongo Transfers over InstaPay/PesoNet) ---

    public async Task<GatewayDisbursement> CreateDisbursementAsync(CreateGatewayDisbursementRequest request, CancellationToken ct = default)
    {
        if (!_options.DisbursementsConfigured)
            throw new PayoutsNotConfiguredException(ProviderName,
                "PayMongo disbursements require PayMongo:SourceAccountNumber/SourceAccountName/SourceAccountBic to be configured.");

        // InstaPay is instant but capped at PHP 50,000 per transfer; larger amounts
        // automatically fall back to PesoNet (batch settlement).
        var provider = _options.DefaultTransferProvider.Trim().ToLowerInvariant();
        if (provider == "instapay" && request.Amount > 50_000m)
            provider = "pesonet";

        // Resolved after the rail is known: some institutions have a different BIC per
        // rail, and the wrong one is rejected downstream.
        var rail = provider == "pesonet" ? TransferRail.Pesonet : TransferRail.Instapay;
        var destinationBic = PayMongoBankCodeMap.Normalize(request.BankCode, rail);

        var payload = new
        {
            transfers = new[]
            {
                new
                {
                    source_account = new
                    {
                        number = _options.SourceAccountNumber,
                        name = _options.SourceAccountName,
                        bic = _options.SourceAccountBic
                    },
                    destination_account = new
                    {
                        number = request.AccountNumber,
                        name = request.AccountHolderName,
                        bic = destinationBic
                    },
                    amount = ToCentavos(request.Amount),
                    currency = request.Currency,
                    provider,
                    reference_number = request.ReferenceId,
                    description = request.Description,
                    purpose = "Disbursement",
                    callback_url = string.IsNullOrWhiteSpace(_options.TransferCallbackUrl) ? null : _options.TransferCallbackUrl
                }
            }
        };

        _logger.LogInformation(
            "Creating PayMongo transfer for {ReferenceId}, amount {Amount} to {Bic}/{AccountNumber} via {Rail}",
            request.ReferenceId, request.Amount, destinationBic, MaskAccountNumber(request.AccountNumber), provider);

        var response = await PostWithIdempotencyAsync("v2/batch_transfers", payload, request.IdempotencyKey, ct);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowTransferFailureAsync(response, "Transfer", ct);
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var transfer = result.GetProperty("data").GetProperty("transfers")[0];

        return ParseTransfer(transfer, request.ReferenceId, request.AccountHolderName);
    }

    /// <summary>
    /// Executes a QR Ph code scanned by the payee-side app, pushing funds out of the
    /// wallet without an account number (POST v3/qr/mpm/execute). The QR string is
    /// passed through verbatim — PayMongo validates it and reports missing fields.
    /// </summary>
    public async Task<GatewayDisbursement> ExecuteQrDisbursementAsync(ExecuteGatewayQrDisbursementRequest request, CancellationToken ct = default)
    {
        if (!_options.DisbursementsConfigured)
            throw new PayoutsNotConfiguredException(ProviderName,
                "PayMongo disbursements require PayMongo:SourceAccountNumber/SourceAccountName/SourceAccountBic to be configured.");

        var payload = new
        {
            qr_string = request.QrString,
            amount = ToCentavos(request.Amount),
            reference_number = request.ReferenceId
        };

        _logger.LogInformation(
            "[PAYMONGO] [QR] Executing QR transfer for {ReferenceId}, amount {Amount}",
            request.ReferenceId, request.Amount);

        var response = await PostWithIdempotencyAsync("v3/qr/mpm/execute", payload, request.IdempotencyKey, ct);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowTransferFailureAsync(response, "QR transfer", ct);
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return ParseQrExecution(result, request.ReferenceId);
    }

    /// <summary>
    /// POSTs with an Idempotency-Key so a retry after a timeout replays the original
    /// response instead of paying twice. A 409 means the first attempt is still in
    /// flight: back off and retry with the SAME key, per PayMongo's best-practices doc.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithIdempotencyAsync(string path, object payload, string? idempotencyKey, CancellationToken ct)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, options: jsonOptions)
            };
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            var response = await _httpClient.SendAsync(message, ct);

            if ((int)response.StatusCode != 409 || attempt >= delays.Length)
                return response;

            var error = await TryReadErrorAsync(response, ct);
            if (!string.Equals(error.Code, "idempotency_in_progress", StringComparison.OrdinalIgnoreCase))
                return response;

            response.Dispose();
            _logger.LogWarning(
                "[PAYMONGO] Idempotent request still in progress, retrying in {Delay}s (attempt {Attempt})",
                delays[attempt].TotalSeconds, attempt + 1);
            await Task.Delay(delays[attempt], ct);
        }
    }

    /// <summary>
    /// Logs the provider's own reason for a failed payout and rethrows as a
    /// <see cref="PaymentGatewayException"/>. Named fields only — the raw body carries PII.
    /// </summary>
    private async Task ThrowTransferFailureAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var error = await TryReadErrorAsync(response, ct);
        _logger.LogError(
            "[PAYMONGO] {Operation} failed - Status: {StatusCode}, ErrorCode: {ErrorCode}, SubCode: {SubCode}, Detail: {Detail}",
            operation, response.StatusCode, error.Code ?? "n/a", error.SubCode ?? "n/a", error.Detail ?? "n/a");

        throw new PaymentGatewayException(ProviderName, (int)response.StatusCode, error.Code,
            $"PayMongo {operation.ToLowerInvariant()} failed with status {(int)response.StatusCode}"
            + (string.IsNullOrWhiteSpace(error.Detail) ? "" : $": {error.Detail}"));
    }

    /// <summary>
    /// The QR execute response wraps the QR resource rather than a transfer, so it gets
    /// its own reader instead of bending <see cref="ParseTransfer"/>. A failed QR transfer
    /// marks the QR "expired"; a successful one marks it "paid".
    /// </summary>
    private static GatewayDisbursement ParseQrExecution(JsonElement result, string fallbackReferenceId)
    {
        var data = result.TryGetProperty("data", out var d) ? d : result;
        var body = data.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs
            : data;

        // Prefer the embedded transfer when present — it carries the authoritative status.
        if (body.TryGetProperty("transfer", out var transfer) && transfer.ValueKind == JsonValueKind.Object)
            return ParseTransfer(transfer, fallbackReferenceId, fallbackAccountHolderName: "");

        var rawStatus = TryGetString(body, "status") ?? "pending";
        var amount = body.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number
            ? FromCentavos(amt.GetInt64())
            : body.TryGetProperty("transaction_amount", out var txAmt) && txAmt.ValueKind == JsonValueKind.Number
                ? FromCentavos(txAmt.GetInt64())
                : 0m;

        return new GatewayDisbursement(
            ProviderDisbursementId: TryGetString(body, "transfer_id")
                                    ?? TryGetString(data, "id")
                                    ?? throw new InvalidOperationException("PayMongo QR execute returned no id"),
            ReferenceId: TryGetString(body, "reference_number") ?? fallbackReferenceId,
            Status: MapQrStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: amount,
            BankCode: "QRPH",
            AccountHolderName: TryGetString(body, "merchant_name") ?? "",
            FailureCode: TryGetString(body, "failure_code") ?? TryGetString(body, "provider_error_code")
        );
    }

    private static GatewayDisbursementStatus MapQrStatus(string rawStatus) => rawStatus.ToLowerInvariant() switch
    {
        "paid" or "succeeded" or "completed" => GatewayDisbursementStatus.Completed,
        "expired" or "failed" or "cancelled" => GatewayDisbursementStatus.Failed,
        _ => GatewayDisbursementStatus.Pending
    };

    public async Task<GatewayDisbursement?> FindDisbursementByReferenceAsync(string referenceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(referenceId)) return null;

        // PayMongo indexes transfers by the reference_number we set on create, which is why
        // every transfer carries "WD-{withdrawalId}". See their Best Practices page.
        var response = await _httpClient.GetAsync(
            $"v2/transfers?reference_number={Uri.EscapeDataString(referenceId)}", ct);

        if (!response.IsSuccessStatusCode)
        {
            // A lookup failure is NOT "no such transfer" — the caller must not treat it as
            // permission to refund, so it is surfaced as an exception rather than null.
            var error = await TryReadErrorAsync(response, ct);
            _logger.LogWarning(
                "[PAYMONGO] Transfer lookup by reference failed - Reference: {Reference}, Status: {StatusCode}, ErrorCode: {ErrorCode}, Detail: {Detail}",
                referenceId, response.StatusCode, error.Code ?? "n/a", error.Detail ?? "n/a");
            throw new PaymentGatewayException(ProviderName, (int)response.StatusCode, error.Code,
                $"PayMongo transfer lookup failed with status {(int)response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        return ParseTransfer(data[0], referenceId, fallbackAccountHolderName: "");
    }

    public async Task<GatewayDisbursement?> GetDisbursementAsync(string providerDisbursementId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v2/transfers/{providerDisbursementId}", ct);
        if (!response.IsSuccessStatusCode) return null;

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var transfer = result.TryGetProperty("data", out var data) ? data : result;

        return ParseTransfer(transfer, fallbackReferenceId: "", fallbackAccountHolderName: "");
    }

    private static GatewayDisbursement ParseTransfer(JsonElement transfer, string fallbackReferenceId, string fallbackAccountHolderName)
    {
        // Batch-transfer responses embed fields directly on the transfer element;
        // tolerate an attributes wrapper for the single-transfer endpoint.
        var body = transfer.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs
            : transfer;

        var rawStatus = TryGetString(body, "status") ?? "pending";

        string? destinationBic = null;
        string? destinationName = null;
        if (body.TryGetProperty("destination_account", out var dest) && dest.ValueKind == JsonValueKind.Object)
        {
            destinationBic = TryGetString(dest, "bic");
            destinationName = TryGetString(dest, "name");
        }

        return new GatewayDisbursement(
            ProviderDisbursementId: transfer.GetProperty("id").GetString()!,
            ReferenceId: TryGetString(body, "reference_number") ?? fallbackReferenceId,
            Status: MapTransferStatus(rawStatus),
            RawStatus: rawStatus,
            Amount: FromCentavos(body.GetProperty("amount").GetInt64()),
            BankCode: destinationBic ?? "",
            AccountHolderName: destinationName ?? fallbackAccountHolderName,
            FailureCode: TryGetString(body, "failure_code")
        );
    }

    // --- Vault (PayMongo customers and payment methods) ---

    public async Task<GatewayCustomer> CreateCustomerAsync(CreateGatewayCustomerRequest request, CancellationToken ct = default)
    {
        // PII: never log the payer email; the reference id is enough to correlate
        _logger.LogInformation("[PAYMONGO] [CUSTOMER] [CREATE] Starting - ReferenceId: {ReferenceId}", request.ReferenceId);

        var payload = new
        {
            data = new
            {
                attributes = new
                {
                    first_name = request.GivenNames,
                    last_name = string.IsNullOrWhiteSpace(request.Surname) ? request.GivenNames : request.Surname,
                    email = request.Email,
                    phone = string.IsNullOrWhiteSpace(request.MobileNumber) ? null : request.MobileNumber,
                    default_device = "email"
                }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("v1/customers", payload,
            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await TryReadErrorCodeAsync(response, ct);
            _logger.LogError("[PAYMONGO] [CUSTOMER] [CREATE] Failed - ReferenceId: {ReferenceId}, Status: {StatusCode}, ErrorCode: {ErrorCode}",
                request.ReferenceId, response.StatusCode, errorCode ?? "n/a");
            throw new PaymentGatewayException(ProviderName, (int)response.StatusCode, errorCode,
                $"PayMongo customer create failed with status {(int)response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var customer = ParseCustomer(result.GetProperty("data"), request.ReferenceId);

        _logger.LogInformation("[PAYMONGO] [CUSTOMER] [CREATE] Success - ReferenceId: {ReferenceId}, CustomerId: {CustomerId}",
            request.ReferenceId, customer.Id);

        return customer;
    }

    public async Task<GatewayCustomer?> GetCustomerAsync(string providerCustomerId, CancellationToken ct = default)
    {
        // NOTE: consumers pass our internal customer Guid on first save (mirroring the
        // Xendit reference-id flow). PayMongo only resolves its own cus_... ids, so a
        // non-PayMongo id simply returns null and the caller creates the customer.
        if (!providerCustomerId.StartsWith("cus_", StringComparison.Ordinal))
            return null;

        var response = await _httpClient.GetAsync($"v1/customers/{providerCustomerId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[PAYMONGO] [CUSTOMER] [GET] Not Found - CustomerId: {CustomerId}, StatusCode: {StatusCode}",
                providerCustomerId, response.StatusCode);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return ParseCustomer(result.GetProperty("data"), referenceIdFallback: "");
    }

    public async Task<GatewayPaymentMethod> CreatePaymentMethodAsync(CreateGatewayPaymentMethodRequest request, CancellationToken ct = default)
    {
        // PayMongo payment methods are created client-side (PayMongo.js) and arrive
        // here as a pm_... id in TokenOrClientKey; "creating" one server-side means
        // resolving and validating it.
        if (string.IsNullOrWhiteSpace(request.TokenOrClientKey))
            throw new ArgumentException("PayMongo requires the client-created payment method id (pm_...)", nameof(request));

        var paymentMethod = await GetPaymentMethodAsync(request.TokenOrClientKey, ct);
        return paymentMethod
            ?? throw new PaymentGatewayException(ProviderName, 404, "resource_not_found",
                "PayMongo payment method not found");
    }

    public async Task<GatewayPaymentMethod?> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v1/payment_methods/{providerPaymentMethodId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[PAYMONGO] [PAYMENT_METHOD] [GET] Not Found - PaymentMethodId: {PaymentMethodId}, StatusCode: {StatusCode}",
                providerPaymentMethodId, response.StatusCode);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = result.GetProperty("data");
        var attributes = data.GetProperty("attributes");
        var type = TryGetString(attributes, "type") ?? "card";

        GatewayCardDetails? card = null;
        GatewayEWalletDetails? eWallet = null;

        if (type == "card" &&
            attributes.TryGetProperty("details", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            string? cardholderName = null;
            if (attributes.TryGetProperty("billing", out var billing) && billing.ValueKind == JsonValueKind.Object)
                cardholderName = TryGetString(billing, "name");

            card = new GatewayCardDetails(
                Last4: TryGetString(details, "last4") ?? "0000",
                Brand: TryGetString(details, "brand"),
                ExpiryMonth: details.TryGetProperty("exp_month", out var expMonth) && expMonth.ValueKind == JsonValueKind.Number ? expMonth.GetInt32() : null,
                ExpiryYear: details.TryGetProperty("exp_year", out var expYear) && expYear.ValueKind == JsonValueKind.Number ? expYear.GetInt32() : null,
                CardholderName: cardholderName
            );
        }
        else if (type != "card")
        {
            eWallet = new GatewayEWalletDetails(ChannelCode: type.ToUpperInvariant());
        }

        return new GatewayPaymentMethod(
            Id: data.GetProperty("id").GetString()!,
            CustomerId: TryGetString(attributes, "customer_id") ?? "",
            Type: type == "card" ? "CARD" : "E_WALLET",
            Status: TryGetString(attributes, "status"),
            Card: card,
            EWallet: eWallet
        );
    }

    public Task DeletePaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct = default)
    {
        // PayMongo has no standalone payment-method delete (detach requires the
        // customer id, which this contract does not carry). Deletion stays local
        // (soft delete); the orphaned pm_... cannot be charged without an intent.
        _logger.LogInformation(
            "[PAYMONGO] [PAYMENT_METHOD] [DELETE] Skipped provider-side delete for {PaymentMethodId} (not supported standalone); local soft delete only",
            providerPaymentMethodId);
        return Task.CompletedTask;
    }

    // --- Mapping helpers ---

    private static GatewayRefundStatus MapRefundStatus(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" => GatewayRefundStatus.Succeeded,
        "failed" => GatewayRefundStatus.Failed,
        "pending" => GatewayRefundStatus.Pending,
        _ => GatewayRefundStatus.Unknown
    };

    private static GatewayDisbursementStatus MapTransferStatus(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" or "completed" => GatewayDisbursementStatus.Completed,
        "failed" or "cancelled" or "returned" => GatewayDisbursementStatus.Failed,
        // pending, processing, queued... are all still in flight
        _ => GatewayDisbursementStatus.Pending
    };

    private static (string Reason, string? Notes) MapRefundReason(string normalizedReason) =>
        normalizedReason.ToUpperInvariant() switch
        {
            "REQUESTED_BY_CUSTOMER" => ("requested_by_customer", null),
            "DUPLICATE" => ("duplicate", null),
            "FRAUDULENT" => ("fraudulent", null),
            _ => ("others", normalizedReason)
        };

    private static GatewayCustomer ParseCustomer(JsonElement data, string referenceIdFallback)
    {
        var attributes = data.GetProperty("attributes");
        return new GatewayCustomer(
            Id: data.GetProperty("id").GetString()!,
            ReferenceId: referenceIdFallback,
            Email: TryGetString(attributes, "email") ?? "",
            GivenNames: TryGetString(attributes, "first_name") ?? "",
            Surname: TryGetString(attributes, "last_name"),
            MobileNumber: TryGetString(attributes, "phone")
        );
    }

    private static string? TryGetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
        => (await TryReadErrorAsync(response, ct)).Code;

    /// <summary>
    /// Pulls the named fields out of PayMongo's error envelope:
    /// { "errors": [ { "code": "...", "sub_code": "...", "detail": "..." } ] }.
    /// Only these three are read — the raw body is never returned or logged, because
    /// transfer errors echo back the destination account holder name and number.
    /// <c>detail</c> is the field that actually says why (e.g. "failed to get organization"
    /// for a bad key); without it a failure is indistinguishable from any other non-2xx.
    /// </summary>
    private static async Task<PayMongoError> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var errorBody = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (errorBody.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                return new PayMongoError(
                    TryGetString(first, "code"),
                    TryGetString(first, "sub_code"),
                    TryGetString(first, "detail"));
            }
        }
        catch (JsonException) { /* non-JSON error body */ }
        return new PayMongoError(null, null, null);
    }

    private readonly record struct PayMongoError(string? Code, string? SubCode, string? Detail);

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
