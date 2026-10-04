using System.Net;
using System.Text.Json;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Payments;

public class XenditGatewayTests
{
    private static (XenditGateway Gateway, CapturingHttpMessageHandler Handler) Create(HttpStatusCode status, string responseBody)
    {
        var handler = new CapturingHttpMessageHandler(status, responseBody);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.xendit.co/") };
        return (new XenditGateway(client, NullLogger<XenditGateway>.Instance), handler);
    }

    [Fact]
    public async Task CreateCheckout_posts_invoice_with_major_units()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"id":"inv-1","external_id":"PAY-1","status":"PENDING","amount":150.50,"invoice_url":"https://inv.url"}
            """);

        var session = await gateway.CreateCheckoutAsync(new CreateCheckoutRequest(
            "PAY-1", 150.50m, "user@test.ph", "Booking payment"));

        Assert.EndsWith("v2/invoices", handler.Request!.RequestUri!.AbsolutePath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);
        Assert.Equal(150.50m, body.GetProperty("amount").GetDecimal()); // Xendit uses major units, not centavos
        Assert.Equal("PAY-1", body.GetProperty("external_id").GetString());

        Assert.Equal(GatewayPaymentStatus.Pending, session.Status);
        Assert.Equal("inv-1", session.ProviderPaymentId);
    }

    [Theory]
    [InlineData("PAID", GatewayPaymentStatus.Paid)]
    [InlineData("SETTLED", GatewayPaymentStatus.Paid)]
    [InlineData("EXPIRED", GatewayPaymentStatus.Expired)]
    [InlineData("PENDING", GatewayPaymentStatus.Pending)]
    [InlineData("SOMETHING_NEW", GatewayPaymentStatus.Unknown)]
    public async Task GetCheckout_normalizes_invoice_statuses(string raw, GatewayPaymentStatus expected)
    {
        var (gateway, _) = Create(HttpStatusCode.OK, $$"""
            {"id":"inv-1","external_id":"PAY-1","status":"{{raw}}","amount":100,"invoice_url":"u","payment_request_id":"pr-1"}
            """);

        var session = await gateway.GetCheckoutAsync("inv-1");

        Assert.Equal(expected, session!.Status);
        Assert.Equal(raw, session.RawStatus);
        Assert.Equal("pr-1", session.ProviderCaptureId);
    }

    [Fact]
    public async Task CreateRefund_sends_idempotency_key_header()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"id":"rf-1","payment_request_id":"pr-1","status":"PENDING","amount":100,"currency":"PHP","reason":"OTHERS"}
            """);

        var refund = await gateway.CreateRefundAsync(new CreateGatewayRefundRequest(
            "pr-1", "OTHERS", 100m, "PHP", "REFUND-PAY-1"));

        Assert.True(handler.Request!.Headers.TryGetValues("idempotency-key", out var values));
        Assert.Equal("REFUND-PAY-1", values!.Single());
        Assert.Equal(GatewayRefundStatus.Pending, refund!.Status);
    }

    [Fact]
    public async Task CreateDisbursement_normalizes_bank_code_and_sends_idempotency_key()
    {
        var (gateway, handler) = Create(HttpStatusCode.OK, """
            {"id":"disb-1","reference_id":"WD-1","status":"ACCEPTED","amount":2500,"channel_code":"PH_BPI","channel_properties":{"account_holder_name":"Juan Cruz"}}
            """);

        var disbursement = await gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
            "WD-1", 2500m, "BPI", "Juan Cruz", "1234567890", "Driver payout", IdempotencyKey: "idem-1"));

        Assert.EndsWith("v2/payouts", handler.Request!.RequestUri!.AbsolutePath);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.RequestBody!);
        Assert.Equal("PH_BPI", body.GetProperty("channel_code").GetString());
        Assert.True(handler.Request.Headers.TryGetValues("Idempotency-key", out var values));
        Assert.Equal("idem-1", values!.Single());

        Assert.Equal(GatewayDisbursementStatus.Pending, disbursement.Status); // ACCEPTED = in flight
        Assert.Equal("disb-1", disbursement.ProviderDisbursementId);
    }

    [Fact]
    public async Task CreateDisbursement_throws_typed_exception_on_provider_error()
    {
        var (gateway, _) = Create(HttpStatusCode.BadRequest, """{"error_code":"INVALID_DESTINATION"}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() =>
            gateway.CreateDisbursementAsync(new CreateGatewayDisbursementRequest(
                "WD-1", 100m, "BPI", "Juan Cruz", "123", "payout")));

        Assert.Equal(PaymentProviders.Xendit, ex.Provider);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("INVALID_DESTINATION", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task GetDisbursement_returns_null_on_not_found()
    {
        var (gateway, _) = Create(HttpStatusCode.NotFound, """{"error_code":"NOT_FOUND"}""");
        Assert.Null(await gateway.GetDisbursementAsync("disb-missing"));
    }
}
