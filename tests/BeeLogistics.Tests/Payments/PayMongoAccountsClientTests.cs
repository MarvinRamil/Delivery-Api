using System.Net;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Behaviour here is pinned to what the live PayMongo API actually did on 2026-08-30, including
/// three places where it contradicts the published documentation. Each of those is a test rather
/// than a comment because the cost of regressing them is either a broken driver flow or money
/// moving out of the wrong wallet.
/// </summary>
public class PayMongoAccountsClientTests
{
    private const string Child = "org_5bff545e74b5f872b1dc6818";
    private const string Parent = "org_bUhq37yJjSDqQTMydiAxqXVJ";

    private static readonly PayMongoOptions Options = new()
    {
        SecretKey = "sk_test_x",
        SourceAccountNumber = "721777442708",
        SourceAccountName = "BEE Logistics",
        SourceAccountBic = "PAEYPHM2XXX"
    };

    private static PayMongoAccountsClient Create(RoutingHttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.paymongo.com/") },
               Microsoft.Extensions.Options.Options.Create(Options),
               NullLogger<PayMongoAccountsClient>.Instance);

    private static RoutingHttpMessageHandler WalletHandler(
        string merchantId = Child,
        string balanceJson = """{"available":2000,"pending":2000}""",
        string accountJson = """{"provider":"paymongo","account_name":"A B","account_number":"285168654745","currency":"PHP","ledger_account_id":"b5089e33"}""")
    {
        string Wallet(string extra) =>
            $$"""{"data":[{"id":"wallet_3e901a16","merchant_id":"{{merchantId}}","status":"activated","type":"default"{{extra}}}]}""";

        return new RoutingHttpMessageHandler()
            .When("v2/wallets/", Wallet(""))
            .When("fields=account", Wallet($",\"account\":{accountJson}"))
            .When("fields=balance", Wallet($",\"balance\":{balanceJson}"));
    }

    private static UpdateChildAccountRequest Details(string? tin = "487-187-167") =>
        new(EmailAddress: "driver+7@bee.test",
            MobileNumber: "+639170000000",
            MiddleName: "D",
            Nationality: "PHL",
            NatureOfWork: "self_employed",
            SourceOfFunds: "commission",
            Tin: tin,
            PlaceOfBirthCity: "Nueva Era",
            PlaceOfBirthCountry: "PH",
            AddressLine1: "Brgy 23",
            AddressCity: "Laoag",
            AddressState: "PH-ILN",
            AddressCountry: "PH",
            AddressPostalCode: "2900");

    // ── Account creation ───────────────────────────────────────────────

    [Fact]
    public async Task CreateConsumerAccount_posts_consumer_type_and_parses_pending_status()
    {
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"data":{"id":"org_new","type":"consumer","activation_status":"pending",
             "person":{"identity_verification_status":"pending"}}}
            """, HttpStatusCode.Created);

        var account = await Create(handler).CreateConsumerAccountAsync();

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        using var sent = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("consumer", sent.RootElement.GetProperty("type").GetString());
        // No person object: PayMongo silently discards one sent at creation - verified live, email
        // and mobile came back null - and activation then fails demanding them. Sending it anyway
        // would look like it worked while quietly losing the driver's contact details.
        Assert.False(sent.RootElement.TryGetProperty("person", out _));
        Assert.Equal("org_new", account.AccountId);
        Assert.Equal(PayMongoActivationStatus.Pending, account.ActivationStatus);
        Assert.False(account.IdentityVerificationPassed);
    }

    [Fact]
    public async Task Duplicate_email_on_update_surfaces_the_provider_detail_rather_than_a_bare_status()
    {
        // PayMongo requires the email to be unique across every account on the platform, so a driver
        // who already has one cannot be onboarded under that address. The caller has to be able to
        // tell this apart from a generic failure to fall back to an alias.
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"errors":[{"code":"parameter_invalid","detail":"The email address is already being used. Kindly use a different email."}]}
            """, HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Create(handler).UpdateAccountAsync(Child, Details()));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("parameter_invalid", ex.ProviderErrorCode);
        Assert.Contains("already being used", ex.Message);
    }

    // ── Identity verification ──────────────────────────────────────────

    [Fact]
    public async Task Verification_session_reads_url_not_the_documented_hosted_url()
    {
        // PayMongo's quick start documents this field as "hosted_url". The live API returns "url",
        // and there is no "hosted_url" at all - reading the documented name hands the driver a null
        // link and silently breaks onboarding.
        var handler = new RoutingHttpMessageHandler().When("identity_verification", """
            {"data":{"id":"verif_23bcf36d","account_id":"org_5bff545e",
             "url":"https://paymongo.powercred.io?access_token=abc","status":"pending","expired_at":1788327423}}
            """);

        var session = await Create(handler).StartIdentityVerificationAsync(Child);

        Assert.Equal("https://paymongo.powercred.io?access_token=abc", session.Url);
        Assert.Equal("verif_23bcf36d", session.VerificationId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788327423).UtcDateTime, session.ExpiresAt);
    }

    [Fact]
    public async Task Verification_session_without_a_url_fails_loudly()
    {
        // Better to fail here than to hand the driver a blank link and have them report it.
        var handler = new RoutingHttpMessageHandler().When("identity_verification",
            """{"data":{"id":"verif_1","status":"pending"}}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Create(handler).StartIdentityVerificationAsync(Child));
        Assert.Equal("missing_url", ex.ProviderErrorCode);
    }

    // ── Activation lifecycle ───────────────────────────────────────────

    [Fact]
    public async Task Pending_account_that_passed_kyc_maps_to_Verified_not_Pending()
    {
        // PayMongo only reports pending/activated/declined. "Passed KYC but not yet activated" is a
        // real state we must be able to see, otherwise a driver stuck mid-onboarding is
        // indistinguishable from one who never started.
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"data":{"id":"org_x","type":"consumer","activation_status":"pending",
             "person":{"first_name":"A","last_name":"B","identity_verification_status":"passed"}}}
            """);

        var account = await Create(handler).GetAccountAsync("org_x");

        Assert.Equal(PayMongoActivationStatus.Verified, account!.ActivationStatus);
        Assert.True(account.IdentityVerificationPassed);
    }

    [Fact]
    public async Task Patching_an_activated_account_surfaces_the_frozen_record_error()
    {
        // Activation is irreversible and makes the account read-only. Callers need the real reason
        // so they do not retry forever against a record that can never change.
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"errors":[{"code":"parameter_invalid","detail":"Account is not in pending activation status."}]}
            """, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Create(handler).UpdateAccountAsync(Child, Details()));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("not in pending activation status", ex.Message);
    }

    // ── Wallet reads: the scope footgun ────────────────────────────────

    [Fact]
    public async Task Wallet_belonging_to_another_account_is_refused()
    {
        // THE important one. On v1 routes PayMongo ignores the Account-Id header entirely and
        // returns the PARENT's wallet with 200 - verified live by sending a nonexistent account id.
        // If a v2 route ever regressed the same way, reading a balance or sending a payout would
        // silently operate on the platform's own money. So the caller asserts ownership rather than
        // trusting the header.
        var handler = WalletHandler(merchantId: Parent);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Create(handler).GetWalletAsync(Child));

        Assert.Equal("scope_mismatch", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task Wallet_read_sends_the_Account_Id_header()
    {
        await Create(WalletHandler()).GetWalletAsync(Child);
        var handler = WalletHandler();
        await Create(handler).GetWalletAsync(Child);

        Assert.All(handler.Requests, r =>
            Assert.Equal(Child, Assert.Single(r.Headers.GetValues("Account-Id"))));
    }

    [Fact]
    public async Task Account_and_balance_are_fetched_as_separate_requests()
    {
        // PayMongo's `fields` parameter takes ONE value per request: ?fields=balance,account returns
        // null for both. Folding them into a single call silently yields a wallet with no account
        // number and no balance.
        var handler = WalletHandler();

        var wallet = await Create(handler).GetWalletAsync(Child, includeAccount: true, includeBalance: true);

        var queries = handler.Requests.Select(r => r.RequestUri!.Query).ToList();
        Assert.Contains(queries, q => q.Contains("fields=account"));
        Assert.Contains(queries, q => q.Contains("fields=balance"));
        Assert.DoesNotContain(queries, q => q.Contains("fields=balance,account") || q.Contains("fields=account,balance"));

        Assert.Equal("285168654745", wallet!.AccountNumber);
        Assert.Equal("b5089e33", wallet.LedgerAccountId);
    }

    [Fact]
    public async Task Balance_is_converted_from_centavos()
    {
        var handler = WalletHandler(balanceJson: """{"available":19402,"pending":19402}""");

        var wallet = await Create(handler).GetWalletAsync(Child, includeBalance: true);

        Assert.Equal(194.02m, wallet!.AvailableBalance);
        Assert.Equal(194.02m, wallet.PendingBalance);
    }

    [Fact]
    public async Task Wallet_type_default_is_reported_so_closed_loop_can_be_detected()
    {
        // A closed-loop wallet can receive but never pay out. Onboarding must be able to notice that
        // before it tells a driver their money is withdrawable.
        var wallet = await Create(WalletHandler()).GetWalletAsync(Child);
        Assert.Equal("default", wallet!.Type);
        Assert.Equal("activated", wallet.Status);
    }

    // ── What activation actually requires (measured, not documented) ───

    [Fact]
    public async Task Update_carries_email_and_mobile_because_creation_drops_them()
    {
        // Activation rejects an account without email_address and mobile_number, and creation
        // silently discards both - so if the PATCH omits them the driver is stuck at activation
        // with an error naming fields the caller believes it already sent.
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"data":{"id":"org_x","type":"consumer","activation_status":"pending","person":{}}}
            """);

        await Create(handler).UpdateAccountAsync(Child, Details());

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        using var sent = System.Text.Json.JsonDocument.Parse(body);
        var person = sent.RootElement.GetProperty("person");
        // Read through the parser: System.Text.Json escapes "+" to \u002B, so the alias is not
        // literally in the payload even though it decodes correctly on the wire.
        Assert.Equal("driver+7@bee.test", person.GetProperty("email_address").GetString());
        Assert.Equal("+639170000000", person.GetProperty("mobile_number").GetString());
    }

    [Fact]
    public async Task Tin_is_optional_and_omitted_when_absent()
    {
        // Proven live by activating an empty child and reading the enumerated required set: tin is
        // not in it, despite the activation guide calling it a prerequisite. That matters - many
        // riders do not have one, and requiring it would gate them out of the platform entirely.
        var handler = new RoutingHttpMessageHandler().When("v2/accounts", """
            {"data":{"id":"org_x","type":"consumer","activation_status":"pending","person":{}}}
            """);

        await Create(handler).UpdateAccountAsync(Child, Details(tin: null));

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        using var sent = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(sent.RootElement.GetProperty("person").TryGetProperty("tin", out _));
    }
}

/// <summary>
/// Wallet QR generation (issue #93).
///
/// <para>These exist because a checkout top-up only reaches the platform wallet on the weekly
/// settlement run — so until it lands, the driver's float is money we have credited but not
/// received. QR Ph settles in real time over InstaPay, which removes that gap entirely.</para>
/// </summary>
public class PayMongoWalletQrTests
{
    private const string Child = "org_03d4711d3f65a6189b624d79";

    private static readonly PayMongoOptions Options = new()
    {
        SecretKey = "sk_test_x",
        SourceAccountNumber = "721777442708",
        SourceAccountName = "BEE Logistics",
        SourceAccountBic = "PAEYPHM2XXX"
    };

    private static PayMongoAccountsClient Create(RoutingHttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.paymongo.com/") },
               Microsoft.Extensions.Options.Options.Create(Options),
               NullLogger<PayMongoAccountsClient>.Instance);

    /// <summary>The live P2P response, trimmed to the fields we read.</summary>
    private const string P2pResponse = """
        {"data":{"id":"qr_094943552dbbbe8a9ec23c6d","mode":"p2p","type":"static","status":"active",
         "merchant_name":"CABONEGRO EDREN VON Tara ","credit_account_number":"817797809438",
         "merchant_id":"03d4711d3f65a6189b624d79","transaction_amount":0,
         "qr_string":"00020101021127590012com.p2pqrpay0111PAEYPHM2XXX"}}
        """;

    private const string P2mDynamicResponse = """
        {"data":{"id":"qr_6f93c1fa752ae8f488f82003","mode":"p2m","type":"dynamic","status":"active",
         "merchant_name":"PMP Ilocos Script","merchant_id":"bUhq37yJjSDqQTMydiAxqXVJ",
         "transaction_amount":5000,"reference_label":"DRVTOPUP-test-1",
         "expires_at":"2026-08-30T15:17:44Z",
         "qr_string":"00020101021228650011ph.ppmi.p2m0111PAEYPHM2XXX"}}
        """;

    [Fact]
    public async Task A_driver_QR_is_scoped_to_their_account_so_it_credits_their_own_wallet()
    {
        // Without Account-Id the QR is the PLATFORM's — it would show our merchant name and credit
        // our wallet, which is the exact behaviour this feature exists to change.
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2pResponse);

        var qr = await Create(handler).GenerateWalletQrAsync(
            new GenerateWalletQrRequest(Child, WalletQrMode.P2P, WalletQrType.Static));

        Assert.Equal(Child, Assert.Single(handler.Requests[0].Headers.GetValues("Account-Id")));
        Assert.Equal("817797809438", qr.CreditAccountNumber);
        Assert.Contains("CABONEGRO", qr.MerchantName);
    }

    [Fact]
    public async Task A_platform_QR_sends_no_Account_Id()
    {
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2mDynamicResponse);

        await Create(handler).GenerateWalletQrAsync(
            new GenerateWalletQrRequest(null, WalletQrMode.P2M, WalletQrType.Dynamic, 50m, "DRVTOPUP-test-1"));

        Assert.False(handler.Requests[0].Headers.Contains("Account-Id"));
    }

    [Fact]
    public async Task Mode_and_type_are_both_sent_and_lowercase()
    {
        // Omitting either fails with "QRPH invalid mode: ", which names neither field. Mode must be
        // lowercase p2p/p2m or PayMongo rejects it "Field 'Mode' validation failed: oneof".
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2pResponse);

        await Create(handler).GenerateWalletQrAsync(
            new GenerateWalletQrRequest(Child, WalletQrMode.P2P, WalletQrType.Static));

        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal("p2p", sent.RootElement.GetProperty("mode").GetString());
        Assert.Equal("static", sent.RootElement.GetProperty("type").GetString());
        Assert.Equal("PH", sent.RootElement.GetProperty("nation").GetString());
        Assert.Equal("PHP", sent.RootElement.GetProperty("transaction_currency").GetString());
    }

    [Fact]
    public async Task A_static_QR_carries_no_amount()
    {
        // Static means "the scanner types the amount". Sending 0 or a stale figure would either be
        // rejected or lock the driver to a number they did not choose.
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2pResponse);

        await Create(handler).GenerateWalletQrAsync(
            new GenerateWalletQrRequest(Child, WalletQrMode.P2P, WalletQrType.Static));

        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.False(sent.RootElement.TryGetProperty("transaction_amount", out _));
    }

    [Fact]
    public async Task A_dynamic_QR_sends_centavos_and_reads_pesos_back()
    {
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2mDynamicResponse);

        var qr = await Create(handler).GenerateWalletQrAsync(
            new GenerateWalletQrRequest(null, WalletQrMode.P2M, WalletQrType.Dynamic, 50m, "DRVTOPUP-test-1"));

        using var sent = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]!);
        Assert.Equal(5000, sent.RootElement.GetProperty("transaction_amount").GetInt64());
        Assert.Equal(50m, qr.Amount);
        Assert.Equal("DRVTOPUP-test-1", qr.ReferenceLabel);
        Assert.NotNull(qr.ExpiresAt);
    }

    [Fact]
    public async Task A_dynamic_QR_without_an_amount_is_refused_before_the_call()
    {
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate", P2mDynamicResponse);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(handler).GenerateWalletQrAsync(
                new GenerateWalletQrRequest(null, WalletQrMode.P2M, WalletQrType.Dynamic)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_QR_with_no_payload_fails_loudly()
    {
        // A blank qr_string cannot be rendered or scanned. Failing beats handing the app an empty
        // code that silently shows nothing.
        var handler = new RoutingHttpMessageHandler().When("v3/qr/mpm/generate",
            """{"data":{"id":"qr_1","mode":"p2p","type":"static","status":"active"}}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            Create(handler).GenerateWalletQrAsync(
                new GenerateWalletQrRequest(Child, WalletQrMode.P2P, WalletQrType.Static)));

        Assert.Equal("missing_qr_string", ex.ProviderErrorCode);
    }
}
