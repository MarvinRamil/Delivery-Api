using System.Net;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Application.Options;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class PayMongoGatewayTests
{
    private static readonly PayMongoOptions Options = new()
    {
        SecretKey = "sk_test_x",
        CheckoutSuccessUrl = "https://app.test/success",
        CheckoutCancelUrl = "https://app.test/cancel",
        DefaultTransferProvider = "instapay",
        SourceAccountNumber = "0001",
        SourceAccountName = "BEE Logistics",
        SourceAccountBic = "PAYMPHM1"
    };

    private static (PayMongoGateway Gateway, CapturingHttpMessageHandler Handler) Create(HttpStatusCode status, string responseBody)
    {
        var handler = new CapturingHttpMessageHandler(status, responseBody);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.paymongo.com/") };
        var gateway = new PayMongoGateway(client, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<PayMongoGateway>.Instance);
        return (gateway, handler);
    }

    [Theory]
    [InlineData(100.00, 10000L)]
    [InlineData(0.01, 1L)]
    [InlineData(1234.56, 123456L)]
    [InlineData(50000.005, 5000001L)] // rounds away from zero
    public void Centavo_conversion_to_provider_units(decimal php, long centavos)
        => Assert.Equal(centavos, PayMongoGateway.ToCentavos(php));

    [Theory]
    [InlineData(10000L, 100.00)]
    [InlineData(1L, 0.01)]
    [InlineData(123456L, 1234.56)]
    public void Centavo_conversion_from_provider_units(long centavos, decimal php)
        => Assert.Equal(php, PayMongoGateway.FromCentavos(centavos));

    [Fact]
    public async Task CreateCheckout_posts_data_attributes_envelope_with_centavos()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"data":{"id":"cs_123","attributes":{"checkout_url":"https://checkout.paymongo.com/cs_123","status":"active"}}}
            """);

        var session = await gateway.CreateCheckoutAsync(new CreateCheckoutRequest(
            "PAY-1", 150.50m, "user@test.ph", "Booking payment"));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.EndsWith("v1/checkout_sessions", handler.Request.RequestUri!.AbsolutePath);

        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);
        var attributes = body.GetProperty("data").GetProperty("attributes");
        Assert.Equal(15050, attributes.GetProperty("line_items")[0].GetProperty("amount").GetInt64());
        Assert.Equal("PAY-1", attributes.GetProperty("reference_number").GetString());
        Assert.Equal("https://app.test/success", attributes.GetProperty("success_url").GetString());

        Assert.Equal("cs_123", session.ProviderPaymentId);
        Assert.Equal("https://checkout.paymongo.com/cs_123", session.CheckoutUrl);
        Assert.Equal(GatewayPaymentStatus.Pending, session.Status);
    }

    [Fact]
    public async Task GetCheckout_maps_paid_payment_and_capture_id()
    {
        var (gateway, _) = Create(HttpStatusCode.OK, """
            {"data":{"id":"cs_123","attributes":{
                "status":"active",
                "reference_number":"PAY-1",
                "checkout_url":"https://checkout.paymongo.com/cs_123",
                "payments":[{"id":"pay_9","attributes":{"status":"paid","amount":15050,"paid_at":1751371200}}]
            }}}
            """);

        var session = await gateway.GetCheckoutAsync("cs_123");

        Assert.NotNull(session);
        Assert.Equal(GatewayPaymentStatus.Paid, session!.Status);
        Assert.Equal("pay_9", session.ProviderCaptureId);
        Assert.Equal(150.50m, session.Amount);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1751371200).UtcDateTime, session.PaidAt);
    }

    [Fact]
    public async Task GetCheckout_maps_expired_session()
    {
        var (gateway, _) = Create(HttpStatusCode.OK, """
            {"data":{"id":"cs_123","attributes":{"status":"expired","reference_number":"PAY-1","checkout_url":"u","line_items":[{"amount":5000}]}}}
            """);

        var session = await gateway.GetCheckoutAsync("cs_123");

        Assert.Equal(GatewayPaymentStatus.Expired, session!.Status);
        Assert.Equal(50m, session.Amount);
    }

    [Fact]
    public async Task GetCheckout_returns_null_on_not_found()
    {
        var (gateway, _) = Create(HttpStatusCode.NotFound, """{"errors":[{"code":"resource_not_found"}]}""");
        Assert.Null(await gateway.GetCheckoutAsync("cs_missing"));
    }

    [Fact]
    public async Task CreateRefund_sends_payment_id_and_centavos_and_maps_status()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"data":{"id":"ref_1","attributes":{"status":"pending","amount":10000,"currency":"PHP","payment_id":"pay_9"}}}
            """);

        var refund = await gateway.CreateRefundAsync(new CreateGatewayRefundRequest(
            "pay_9", "REQUESTED_BY_CUSTOMER", 100m, "PHP", "REFUND-PAY-1"));

        Assert.EndsWith("v1/refunds", handler.Request!.RequestUri!.AbsolutePath);
        var attributes = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!)
            .GetProperty("data").GetProperty("attributes");
        Assert.Equal(10000, attributes.GetProperty("amount").GetInt64());
        Assert.Equal("pay_9", attributes.GetProperty("payment_id").GetString());
        Assert.Equal("requested_by_customer", attributes.GetProperty("reason").GetString());

        Assert.NotNull(refund);
        Assert.Equal("ref_1", refund!.ProviderRefundId);
        Assert.Equal(GatewayRefundStatus.Pending, refund.Status);
        Assert.Equal(100m, refund.Amount);
    }

    [Fact]
    public async Task CreateRefund_maps_unlisted_reason_to_others_with_notes()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"data":{"id":"ref_1","attributes":{"status":"succeeded","amount":10000,"currency":"PHP","payment_id":"pay_9"}}}
            """);

        var refund = await gateway.CreateRefundAsync(new CreateGatewayRefundRequest("pay_9", "CANCELLATION", 100m));

        var attributes = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!)
            .GetProperty("data").GetProperty("attributes");
        Assert.Equal("others", attributes.GetProperty("reason").GetString());
        Assert.Equal("CANCELLATION", attributes.GetProperty("notes").GetString());
        Assert.Equal(GatewayRefundStatus.Succeeded, refund!.Status);
    }

    [Fact]
    public async Task CreateRefund_returns_null_on_provider_rejection()
    {
        var (gateway, _) = Create(HttpStatusCode.BadRequest, """{"errors":[{"code":"insufficient_funds"}]}""");
        Assert.Null(await gateway.CreateRefundAsync(new CreateGatewayRefundRequest("pay_9", "OTHERS", 100m)));
    }

    [Fact]
    public async Task CreateDisbursement_posts_batch_transfer_with_source_and_bic()
    {
        var (gateway, handler) = Create(HttpStatusCode.Created, """
            {"data":{"id":"batch_tr_1","transfers":[{"id":"tr_1","amount":250000,"currency":"PHP","provider":"instapay","status":"pending","reference_number":"WD-1","destination_account":{"bic":"BOPIPHMMXXX","name":"Juan Cruz"}}]}}
            """);

        var disbursement = await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
            "WD-1", 2500m, "BPI", "Juan Cruz", "1234567890", "Driver payout"));

        Assert.EndsWith("v2/batch_transfers", handler.Request!.RequestUri!.AbsolutePath);
        var transfer = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!).GetProperty("transfers")[0];
        Assert.Equal(250000, transfer.GetProperty("amount").GetInt64());
        Assert.Equal("instapay", transfer.GetProperty("provider").GetString());
        Assert.Equal("BOPIPHMMXXX", transfer.GetProperty("destination_account").GetProperty("bic").GetString());
        Assert.Equal("0001", transfer.GetProperty("source_account").GetProperty("number").GetString());
        Assert.Equal("WD-1", transfer.GetProperty("reference_number").GetString());

        Assert.Equal("tr_1", disbursement.ProviderDisbursementId);
        Assert.Equal(GatewayDisbursementStatus.Pending, disbursement.Status);
        Assert.Equal(2500m, disbursement.Amount);
    }

    [Fact]
    public async Task CreateDisbursement_sends_idempotency_key_and_callback_url()
    {
        // PayMongo replays the first response for a key for ~24h. Without the header a retry
        // after a timeout pays the driver twice, which is why POST retries were disabled.
        var options = new PayMongoOptions
        {
            SecretKey = Options.SecretKey,
            DefaultTransferProvider = "instapay",
            SourceAccountNumber = "0001",
            SourceAccountName = "BEE Logistics",
            SourceAccountBic = "PAYMPHM1",
            TransferCallbackUrl = "https://api.bee.test/api/webhooks/paymongo"
        };
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.Created, """
            {"data":{"id":"batch_tr_1","transfers":[{"id":"tr_1","amount":10000,"currency":"PHP","status":"pending","reference_number":"WD-1"}]}}
            """);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.paymongo.com/") };
        var gateway = new PayMongoGateway(client,
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<PayMongoGateway>.Instance);

        await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
            "WD-1", 100m, "BPI", "Juan Cruz", "1234567890", "Driver payout", IdempotencyKey: "wd-abc-123"));

        Assert.Equal("wd-abc-123", Assert.Single(handler.Request!.Headers.GetValues("Idempotency-Key")));
        var transfer = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!).GetProperty("transfers")[0];
        Assert.Equal("https://api.bee.test/api/webhooks/paymongo", transfer.GetProperty("callback_url").GetString());
        Assert.Equal("Disbursement", transfer.GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task CreateDisbursement_omits_callback_url_when_not_configured()
    {
        // Null fields are dropped rather than sent as null — PayMongo rejects a null callback_url.
        var (gateway, handler) = Create(HttpStatusCode.Created, """
            {"data":{"id":"batch_tr_1","transfers":[{"id":"tr_1","amount":10000,"currency":"PHP","status":"pending","reference_number":"WD-1"}]}}
            """);

        await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
            "WD-1", 100m, "BPI", "Juan Cruz", "1234567890", "Driver payout"));

        var transfer = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!).GetProperty("transfers")[0];
        Assert.False(transfer.TryGetProperty("callback_url", out _));
    }

    [Fact]
    public async Task Provider_error_detail_reaches_the_exception_message()
    {
        // "failed to get organization" is what a whsk_ in the secret-key slot actually returns.
        // Logging only errors[0].code loses it, and with it the only clue to the real cause.
        var (gateway, _) = Create(HttpStatusCode.Unauthorized,
            """{"errors":[{"code":"unauthorized","detail":"failed to get organization"}]}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
                "WD-1", 100m, "BPI", "Juan Cruz", "123", "payout")));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("failed to get organization", ex.Message);
    }

    [Fact]
    public async Task ExecuteQrDisbursement_posts_the_scanned_qr_string_verbatim()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"data":{"id":"qr_1","attributes":{"status":"paid","amount":50000,"reference_number":"WD-2","transfer_id":"tr_9"}}}
            """);

        var qr = "00020101021227590012com.p2pqrpay0111PAEYPHM2XXX0208999644030412999999990001";
        var disbursement = await gateway.ExecuteQrDisbursementAsync(
            new ExecuteGatewayQrDisbursementRequest("WD-2", 500m, qr, IdempotencyKey: "wd-qr-1"));

        Assert.EndsWith("v3/qr/mpm/execute", handler.Request!.RequestUri!.AbsolutePath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);
        Assert.Equal(qr, body.GetProperty("qr_string").GetString());
        Assert.Equal(50000, body.GetProperty("amount").GetInt64());
        Assert.Equal("WD-2", body.GetProperty("reference_number").GetString());
        Assert.Equal("wd-qr-1", Assert.Single(handler.Request.Headers.GetValues("Idempotency-Key")));

        // "paid" on the QR means the transfer behind it settled.
        Assert.Equal(GatewayDisbursementStatus.Completed, disbursement.Status);
        Assert.Equal("tr_9", disbursement.ProviderDisbursementId);
        Assert.Equal(500m, disbursement.Amount);
    }

    [Fact]
    public async Task ExecuteQrDisbursement_maps_expired_qr_to_failed()
    {
        // A failed QR transfer is reported by expiring the QR, not by a "failed" status.
        var (gateway, _) = Create(HttpStatusCode.OK, """
            {"data":{"id":"qr_2","attributes":{"status":"expired","amount":50000,"reference_number":"WD-3","transfer_id":"tr_10"}}}
            """);

        var disbursement = await gateway.ExecuteQrDisbursementAsync(
            new ExecuteGatewayQrDisbursementRequest("WD-3", 500m, "00020101021227590012com.p2pqrpay"));

        Assert.Equal(GatewayDisbursementStatus.Failed, disbursement.Status);
    }

    [Fact]
    public async Task CreateDisbursement_falls_back_to_pesonet_above_instapay_cap()
    {
        var (gateway, handler) = Create(HttpStatusCode.Created, """
            {"data":{"id":"batch_tr_1","transfers":[{"id":"tr_1","amount":6000000,"currency":"PHP","provider":"pesonet","status":"pending","reference_number":"WD-1"}]}}
            """);

        await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
            "WD-1", 60_000m, "BDO", "Juan Cruz", "1234567890", "Driver payout"));

        var transfer = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!).GetProperty("transfers")[0];
        Assert.Equal("pesonet", transfer.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task CreateDisbursement_throws_typed_exception_on_provider_error()
    {
        var (gateway, _) = Create(HttpStatusCode.BadRequest, """{"errors":[{"code":"invalid_account"}]}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
                "WD-1", 100m, "BPI", "Juan Cruz", "123", "payout")));

        Assert.Equal(PaymentProviders.PayMongo, ex.Provider);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_account", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task CreateDisbursement_without_source_account_config_throws()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.OK, "{}");
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.paymongo.com/") };
        var gateway = new PayMongoGateway(client,
            Microsoft.Extensions.Options.Options.Create(new PayMongoOptions { SecretKey = "sk_test_x" }),
            NullLogger<PayMongoGateway>.Instance);

        // PayoutsNotConfiguredException (an InvalidOperationException) rather than a bare
        // one, so the withdrawal handler can tell "we are misconfigured" apart from
        // "the provider is down" and stop telling drivers to try again.
        await Assert.ThrowsAsync<PayoutsNotConfiguredException>(() =>
            gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
                "WD-1", 100m, "BPI", "Juan Cruz", "123", "payout")));
        Assert.Null(handler.Request); // never reached the wire
    }

    [Fact]
    public async Task GetCustomer_with_internal_reference_id_returns_null_without_calling_api()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, "{}");
        var result = await gateway.GetCustomerAsync(Guid.NewGuid().ToString());
        Assert.Null(result);
        Assert.Null(handler.Request);
    }
}
