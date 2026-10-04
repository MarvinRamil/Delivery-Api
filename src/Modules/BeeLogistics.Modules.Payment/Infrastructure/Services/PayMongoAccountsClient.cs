using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Payment.Infrastructure.Services;

/// <summary>
/// PayMongo Platforms API adapter: child accounts and their wallets.
///
/// <para><b>Everything here was verified against the live API on 2026-08-30.</b> Several behaviours
/// contradict the published docs or fail silently, and each is guarded below rather than trusted:</para>
/// <list type="bullet">
///   <item>The <c>Account-Id</c> header scopes requests to a child, but <b>only on v2 routes</b>.
///   On v1 it is silently ignored: a request carrying a deliberately invalid account id returns the
///   <i>parent's</i> wallet with 200. Every wallet read here therefore asserts the returned
///   <c>merchant_id</c> — see <see cref="GetWalletAsync"/>.</item>
///   <item>The <c>fields</c> query parameter accepts <b>one value per request</b>.
///   <c>?fields=balance,account</c> returns null for both, so account and balance are two calls.</item>
///   <item>Identity verification returns <c>url</c>, not <c>hosted_url</c> as documented.</item>
///   <item><c>POST /v2/accounts</c> <b>ignores the person object entirely</b>. Email and mobile
///   must be set by the subsequent PATCH or activation fails demanding them.</item>
///   <item><c>tin</c> is <b>not</b> required for consumer activation, despite the activation guide
///   listing it as a prerequisite. Confirmed by activating an empty child and reading the
///   enumerated required set.</item>
/// </list>
/// </summary>
public sealed class PayMongoAccountsClient : IPayMongoAccountsClient
{
    private const string Provider = PaymentProviders.PayMongo;

    /// <summary>Every PayMongo wallet sends and receives as this BIC, parent and child alike.</summary>
    private const string PayMongoWalletBic = "PAEYPHM2XXX";

    /// <summary>In-network rail. Not instapay/pesonet, and not chargeable.</summary>
    private const string InternalProvider = "paymongo";

    private readonly HttpClient _httpClient;
    private readonly PayMongoOptions _options;
    private readonly ILogger<PayMongoAccountsClient> _logger;

    public PayMongoAccountsClient(
        HttpClient httpClient,
        IOptions<PayMongoOptions> options,
        ILogger<PayMongoAccountsClient> logger)
    {
        // Base address and Basic auth come from the typed-client registration in DependencyInjection.
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PayMongoChildAccount> CreateConsumerAccountAsync(CancellationToken ct = default)
    {
        // Type only. PayMongo SILENTLY DISCARDS any person object sent here - verified live: email
        // and mobile passed at creation come back null, and activation then fails demanding them.
        // Their quick start shows them in this body anyway. Person data goes in UpdateAccountAsync.
        var payload = new { type = PayMongoAccountTypes.Consumer };

        var response = await _httpClient.PostAsJsonAsync("v2/accounts", payload, JsonOpts, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "create child account", ct);

        var account = ParseAccount(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
        _logger.LogInformation(
            "[PAYMONGO] [ACCOUNTS] Created child account {AccountId} ({Status})",
            account.AccountId, account.ActivationStatus);
        return account;
    }

    public async Task<PayMongoVerificationSession> StartIdentityVerificationAsync(
        string accountId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync(
            $"v2/accounts/{accountId}/identity_verification", content: null, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "start identity verification", ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = body.TryGetProperty("data", out var d) ? d : body;

        // The session may be wrapped in "attributes" or flat depending on the route; take whichever
        // actually carries the fields rather than assuming a shape.
        var src = data.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
            ? attrs
            : data;

        // "url" is correct here. The quick start says "hosted_url", which is always absent —
        // reading that name hands the driver a null link.
        var url = TryGetString(src, "url") ?? TryGetString(src, "hosted_url");
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogError(
                "[PAYMONGO] [ACCOUNTS] Verification session for {AccountId} returned no url", accountId);
            throw new PaymentGatewayException(Provider, (int)response.StatusCode, "missing_url",
                "PayMongo returned an identity verification session without a URL.");
        }

        return new PayMongoVerificationSession(
            TryGetString(data, "id") ?? "",
            url,
            TryGetString(src, "status") ?? "pending",
            TryGetString(src, "result"),
            TryGetString(src, "failure_reason"),
            ParseExpiry(src));
    }

    public async Task<PayMongoChildAccount> UpdateAccountAsync(
        string accountId, UpdateChildAccountRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            person = new
            {
                email_address = request.EmailAddress,
                mobile_number = request.MobileNumber,
                middle_name = request.MiddleName,
                nationality = request.Nationality,
                nature_of_work = request.NatureOfWork,
                source_of_funds = request.SourceOfFunds,
                source_of_funds_other = request.SourceOfFundsOther,
                tin = request.Tin,
                place_of_birth = new
                {
                    city = request.PlaceOfBirthCity,
                    country = request.PlaceOfBirthCountry
                },
                address = new
                {
                    line1 = request.AddressLine1,
                    city = request.AddressCity,
                    state = request.AddressState,
                    country = request.AddressCountry,
                    postal_code = request.AddressPostalCode
                }
            }
        };

        var response = await _httpClient.PatchAsJsonAsync($"v2/accounts/{accountId}", payload, JsonOpts, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "update child account", ct);

        return ParseAccount(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    public async Task<PayMongoChildAccount> ActivateAccountAsync(string accountId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync($"v2/accounts/{accountId}/activate", content: null, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "activate child account", ct);

        var account = ParseAccount(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
        _logger.LogInformation(
            "[PAYMONGO] [ACCOUNTS] Activated child account {AccountId} ({Status})",
            account.AccountId, account.ActivationStatus);
        return account;
    }

    public async Task<PayMongoChildAccount?> GetAccountAsync(string accountId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync($"v2/accounts/{accountId}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "get child account", ct);

        return ParseAccount(await response.Content.ReadFromJsonAsync<JsonElement>(ct));
    }

    public async Task<PayMongoChildWallet?> GetWalletAsync(
        string accountId, bool includeAccount = false, bool includeBalance = false, CancellationToken ct = default)
    {
        var wallet = await ReadWalletAsync(accountId, field: null, ct);
        if (wallet is null) return null;

        // One field per request: PayMongo returns null for every requested field when several are
        // comma-separated, so these cannot be folded into the call above or into each other.
        string? accountNumber = null, accountName = null, ledgerAccountId = null;
        decimal? available = null, pending = null;

        if (includeAccount)
        {
            var withAccount = await ReadWalletAsync(accountId, field: "account", ct);
            if (withAccount?.Account is { } acct)
            {
                accountNumber = TryGetString(acct, "account_number");
                accountName = TryGetString(acct, "account_name");
                ledgerAccountId = TryGetString(acct, "ledger_account_id");
            }
        }

        if (includeBalance)
        {
            var withBalance = await ReadWalletAsync(accountId, field: "balance", ct);
            if (withBalance?.Balance is { } bal)
            {
                available = TryGetCentavos(bal, "available");
                pending = TryGetCentavos(bal, "pending");
            }
        }

        return new PayMongoChildWallet(
            wallet.WalletId, wallet.MerchantId, wallet.Status, wallet.Type,
            accountNumber, accountName, ledgerAccountId, available, pending);
    }

    /// <summary>
    /// Reads the child's default wallet, asserting it actually belongs to the requested account.
    /// <para>
    /// The assertion is not defensive padding. On v1 routes PayMongo ignores <c>Account-Id</c>
    /// entirely and returns the parent's wallet with 200 — verified by sending a nonexistent account
    /// id and getting the platform wallet back. Were a v2 route to regress the same way, reading a
    /// balance or sending a payout would silently operate on the platform's own money.
    /// </para>
    /// </summary>
    private async Task<RawWallet?> ReadWalletAsync(string accountId, string? field, CancellationToken ct)
    {
        var path = field is null ? "v2/wallets/" : $"v2/wallets/?fields={field}";
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Add("Account-Id", accountId);

        var response = await _httpClient.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "get child wallet", ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var wallet = data[0];
        var merchantId = TryGetString(wallet, "merchant_id");

        if (!string.Equals(merchantId, accountId, StringComparison.Ordinal))
        {
            _logger.LogCritical(
                "[PAYMONGO] [ACCOUNTS] [SCOPE_MISMATCH] Asked for wallet of {AccountId} but PayMongo returned "
                + "one owned by {MerchantId}. The Account-Id header was not honoured; refusing to use this wallet.",
                accountId, merchantId ?? "n/a");
            throw new PaymentGatewayException(Provider, (int)response.StatusCode, "scope_mismatch",
                "PayMongo returned a wallet belonging to a different account than the one requested.");
        }

        return new RawWallet(
            TryGetString(wallet, "id") ?? "",
            merchantId ?? "",
            TryGetString(wallet, "status") ?? "",
            TryGetString(wallet, "type") ?? "",
            wallet.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object ? a : null,
            wallet.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Object ? b : null);
    }

    private sealed record RawWallet(
        string WalletId, string MerchantId, string Status, string Type,
        JsonElement? Account, JsonElement? Balance);

    private static PayMongoChildAccount ParseAccount(JsonElement body)
    {
        var data = body.TryGetProperty("data", out var d) ? d : body;
        var person = data.TryGetProperty("person", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : (JsonElement?)null;

        var idvPassed = person is not null &&
            string.Equals(TryGetString(person.Value, "identity_verification_status"), "passed",
                StringComparison.OrdinalIgnoreCase);

        return new PayMongoChildAccount(
            TryGetString(data, "id") ?? "",
            TryGetString(data, "type") ?? PayMongoAccountTypes.Consumer,
            MapActivationStatus(TryGetString(data, "activation_status"), idvPassed),
            person is null ? null : TryGetString(person.Value, "first_name"),
            person is null ? null : TryGetString(person.Value, "last_name"),
            idvPassed);
    }

    /// <summary>
    /// PayMongo reports only pending / activated / declined. The finer states we track locally are
    /// derived from whether identity verification has passed, so a driver stuck at KYC is
    /// distinguishable from one who has simply not started.
    /// </summary>
    private static PayMongoActivationStatus MapActivationStatus(string? raw, bool identityVerificationPassed)
        => raw?.ToLowerInvariant() switch
        {
            "activated" => PayMongoActivationStatus.Activated,
            "declined" => PayMongoActivationStatus.Declined,
            "pending" => identityVerificationPassed
                ? PayMongoActivationStatus.Verified
                : PayMongoActivationStatus.Pending,
            _ => PayMongoActivationStatus.Pending
        };

    private static DateTime? ParseExpiry(JsonElement src)
    {
        if (!src.TryGetProperty("expired_at", out var exp)) return null;
        return exp.ValueKind switch
        {
            JsonValueKind.Number when exp.TryGetInt64(out var unix) =>
                DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
            JsonValueKind.String when DateTime.TryParse(exp.GetString(), out var parsed) =>
                parsed.ToUniversalTime(),
            _ => null
        };
    }

    private async Task ThrowAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        string? code = null, detail = null;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                code = TryGetString(errors[0], "code");
                detail = TryGetString(errors[0], "detail");
            }
        }
        catch (JsonException) { /* non-JSON error body */ }

        // Never log the raw body: account responses carry the driver's name, birth date, TIN and address.
        _logger.LogError(
            "[PAYMONGO] [ACCOUNTS] {Operation} failed - Status: {StatusCode}, Code: {Code}, Detail: {Detail}",
            operation, (int)response.StatusCode, code ?? "n/a", detail ?? "n/a");

        throw new PaymentGatewayException(Provider, (int)response.StatusCode, code,
            $"PayMongo {operation} failed with status {(int)response.StatusCode}"
            + (string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}"));
    }

    public Task<PayMongoInternalTransfer> TransferToChildAsync(
        string accountId, string destinationAccountNumber, string destinationAccountName,
        decimal amount, string referenceNumber, string description, CancellationToken ct = default)
    {
        if (!_options.DisbursementsConfigured)
            throw new PayoutsNotConfiguredException(Provider,
                "PayMongo source account is not configured; cannot fund driver wallets.");

        // Platform -> driver. Sent as the platform (no Account-Id): the money leaves our wallet.
        return SendInternalTransferAsync(
            onBehalfOfAccountId: null,
            source: (_options.SourceAccountNumber, _options.SourceAccountName),
            destination: (destinationAccountNumber, destinationAccountName),
            amount, referenceNumber, description, accountId, "credit child wallet", ct);
    }

    public Task<PayMongoInternalTransfer> SweepFromChildAsync(
        string accountId, string sourceAccountNumber, string sourceAccountName,
        decimal amount, string referenceNumber, string description, CancellationToken ct = default)
    {
        if (!_options.DisbursementsConfigured)
            throw new PayoutsNotConfiguredException(Provider,
                "PayMongo source account is not configured; cannot sweep driver wallets.");

        // Driver -> platform. Sent AS the child, so Account-Id is required: the money leaves their
        // wallet, not ours.
        return SendInternalTransferAsync(
            onBehalfOfAccountId: accountId,
            source: (sourceAccountNumber, sourceAccountName),
            destination: (_options.SourceAccountNumber, _options.SourceAccountName),
            amount, referenceNumber, description, accountId, "sweep child wallet", ct);
    }

    public async Task<PayMongoInternalTransfer> WithdrawFromChildAsync(
        ChildWalletPayoutRequest request, CancellationToken ct = default)
    {
        if (request.Amount <= 0)
            throw new ArgumentException("Withdrawal amount must be positive", nameof(request));

        var payload = new
        {
            transfers = new[]
            {
                new
                {
                    provider = request.Provider,
                    amount = ToCentavos(request.Amount),
                    currency = "PHP",
                    purpose = "Disbursement",
                    description = request.Description,
                    reference_number = request.ReferenceNumber,
                    // The CHILD's wallet, not ours - this money is the driver's.
                    source_account = new
                    {
                        number = request.SourceAccountNumber,
                        name = request.SourceAccountName,
                        bic = PayMongoWalletBic
                    },
                    destination_account = new
                    {
                        number = request.DestinationAccountNumber,
                        name = request.DestinationAccountName,
                        bic = request.DestinationBic
                    },
                    callback_url = string.IsNullOrWhiteSpace(_options.TransferCallbackUrl)
                        ? null
                        : _options.TransferCallbackUrl
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v2/batch_transfers")
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };
        // Required: without it PayMongo would debit the PLATFORM wallet instead of the driver's.
        message.Headers.Add("Account-Id", request.AccountId);

        var response = await _httpClient.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "withdraw from child wallet", ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var transfer = ReadFirstTransfer(body);

        var result = new PayMongoInternalTransfer(
            TryGetString(transfer, "id") ?? request.ReferenceNumber,
            TryGetString(transfer, "status") ?? "pending",
            request.Amount,
            transfer.TryGetProperty("fee", out var fee) && fee.ValueKind == JsonValueKind.Number
                ? fee.GetInt64() / 100m
                : 0m,
            TryGetString(transfer, "provider_error_message"));

        _logger.LogInformation(
            "[PAYMONGO] [ACCOUNTS] Withdrew {Amount} from {AccountId} over {Rail}: {TransferId} {Status} (fee {Fee})",
            request.Amount, request.AccountId, request.Provider, result.TransferId, result.Status, result.Fee);

        return result;
    }

    public async Task<PayMongoWalletQr> GenerateWalletQrAsync(
        GenerateWalletQrRequest request, CancellationToken ct = default)
    {
        if (request.Type == WalletQrType.Dynamic && request.Amount is not > 0)
            throw new ArgumentException("A dynamic QR needs a positive amount", nameof(request));

        // mode and type are BOTH required and are separate fields. Sending only one fails with
        // "QRPH invalid mode: ", which names neither. mode is lowercase p2p/p2m - anything else is
        // rejected "Field 'Mode' validation failed: oneof".
        var payload = new
        {
            mode = request.Mode == WalletQrMode.P2P ? "p2p" : "p2m",
            type = request.Type == WalletQrType.Dynamic ? "dynamic" : "static",
            nation = "PH",
            transaction_currency = "PHP",
            transaction_amount = request.Type == WalletQrType.Dynamic
                ? ToCentavos(request.Amount!.Value)
                : (long?)null,
            reference_label = request.ReferenceLabel
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v3/qr/mpm/generate")
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };

        // Without this the QR is the PLATFORM's: it would show our merchant name and credit our
        // wallet, which is exactly the behaviour this feature exists to change.
        if (request.OnBehalfOfAccountId is not null)
            message.Headers.Add("Account-Id", request.OnBehalfOfAccountId);

        var response = await _httpClient.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "generate wallet QR", ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = body.TryGetProperty("data", out var d) ? d : body;

        var qrString = TryGetString(data, "qr_string");
        if (string.IsNullOrWhiteSpace(qrString))
        {
            // A QR with no payload cannot be rendered or scanned. Better to fail than to hand the
            // app a blank code that silently shows nothing.
            _logger.LogError(
                "[PAYMONGO] [QR] Generated QR {QrId} carried no qr_string", TryGetString(data, "id") ?? "n/a");
            throw new PaymentGatewayException(Provider, (int)response.StatusCode, "missing_qr_string",
                "PayMongo returned a QR without a payload.");
        }

        var qr = new PayMongoWalletQr(
            TryGetString(data, "id") ?? "",
            qrString,
            TryGetString(data, "mode") ?? "",
            TryGetString(data, "type") ?? "",
            TryGetString(data, "status") ?? "",
            TryGetString(data, "merchant_name"),
            TryGetString(data, "credit_account_number"),
            TryGetString(data, "merchant_id"),
            data.TryGetProperty("transaction_amount", out var amt) && amt.ValueKind == JsonValueKind.Number
                ? amt.GetInt64() / 100m
                : null,
            TryGetString(data, "reference_label"),
            ParseTimestamp(data, "expires_at"));

        _logger.LogInformation(
            "[PAYMONGO] [QR] Generated {Mode}/{Type} QR {QrId} for {Target} (amount {Amount})",
            qr.Mode, qr.Type, qr.QrId,
            request.OnBehalfOfAccountId ?? "platform", qr.Amount?.ToString("N2") ?? "any");

        return qr;
    }

    private static DateTime? ParseTimestamp(JsonElement src, string name)
        => src.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    public async Task<PayMongoInternalTransfer?> GetChildTransferAsync(
        string accountId, string transferId, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"v2/transfers/{transferId}");
        message.Headers.Add("Account-Id", accountId);

        var response = await _httpClient.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, "get child transfer", ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var data = body.TryGetProperty("data", out var d) ? d : body;

        // Confirm the transfer really belongs to this child. Account-Id is honoured on v2, but a
        // reconciler settles withdrawals unattended - if scoping ever regressed, this would settle
        // one driver's withdrawal against another account's transfer.
        var merchantId = TryGetString(data, "merchant_id");
        if (merchantId is not null && !string.Equals(merchantId, accountId, StringComparison.Ordinal))
        {
            _logger.LogCritical(
                "[PAYMONGO] [ACCOUNTS] [SCOPE_MISMATCH] Transfer {TransferId} belongs to {MerchantId}, "
                + "not {AccountId}; refusing to reconcile against it.",
                transferId, merchantId, accountId);
            throw new PaymentGatewayException(Provider, (int)response.StatusCode, "scope_mismatch",
                "PayMongo returned a transfer belonging to a different account than the one requested.");
        }

        return new PayMongoInternalTransfer(
            TryGetString(data, "id") ?? transferId,
            TryGetString(data, "status") ?? "pending",
            data.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Number
                ? amt.GetInt64() / 100m
                : 0m,
            data.TryGetProperty("fee", out var f) && f.ValueKind == JsonValueKind.Number
                ? f.GetInt64() / 100m
                : 0m,
            TryGetString(data, "provider_error_message"));
    }

    private async Task<PayMongoInternalTransfer> SendInternalTransferAsync(
        string? onBehalfOfAccountId,
        (string Number, string Name) source,
        (string Number, string Name) destination,
        decimal amount,
        string referenceNumber,
        string description,
        string accountId,
        string operation,
        CancellationToken ct)
    {
        if (amount <= 0)
            throw new ArgumentException("Transfer amount must be positive", nameof(amount));

        var payload = new
        {
            transfers = new[]
            {
                new
                {
                    provider = InternalProvider,
                    amount = ToCentavos(amount),
                    currency = "PHP",
                    purpose = "Disbursement",
                    description,
                    reference_number = referenceNumber,
                    source_account = new { number = source.Number, name = source.Name, bic = PayMongoWalletBic },
                    destination_account = new { number = destination.Number, name = destination.Name, bic = PayMongoWalletBic }
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v2/batch_transfers")
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };
        if (onBehalfOfAccountId is not null)
            message.Headers.Add("Account-Id", onBehalfOfAccountId);

        var response = await _httpClient.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            await ThrowAsync(response, operation, ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var transfer = ReadFirstTransfer(body);

        var result = new PayMongoInternalTransfer(
            TryGetString(transfer, "id") ?? referenceNumber,
            TryGetString(transfer, "status") ?? "pending",
            amount,
            // Read, never assumed: the fee PayMongo charges has varied between channels, and a
            // non-zero fee on a FAILED transfer is a quote rather than a charge.
            transfer.TryGetProperty("fee", out var fee) && fee.ValueKind == JsonValueKind.Number
                ? fee.GetInt64() / 100m
                : 0m,
            // The only field carrying a reason. There is no failure_code/failure_message.
            TryGetString(transfer, "provider_error_message"));

        _logger.LogInformation(
            "[PAYMONGO] [ACCOUNTS] {Operation} {Amount} for {AccountId}: {TransferId} {Status} (fee {Fee})",
            operation, amount, accountId, result.TransferId, result.Status, result.Fee);

        return result;
    }

    /// <summary>
    /// A batch transfer response wraps the transfers it spawned. We always send exactly one.
    /// </summary>
    private static JsonElement ReadFirstTransfer(JsonElement body)
    {
        var data = body.TryGetProperty("data", out var d) ? d : body;
        if (data.TryGetProperty("transfers", out var transfers) &&
            transfers.ValueKind == JsonValueKind.Array && transfers.GetArrayLength() > 0)
            return transfers[0];
        return data;
    }

    private static long ToCentavos(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>PayMongo reports balances in integer centavos.</summary>
    private static decimal? TryGetCentavos(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64() / 100m
            : null;
}
