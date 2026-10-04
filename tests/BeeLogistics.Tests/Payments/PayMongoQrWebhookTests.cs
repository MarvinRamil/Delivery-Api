using System.Text.Json;
using BeeLogistics.Modules.Payment.Presentation.Controllers;
using Xunit;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Reading a <c>qr.paid</c> payload (issue #93). Every quirk covered here fails <b>silently</b>
/// when read wrong — the money is already in the driver's PayMongo wallet by the time this webhook
/// arrives, so a misread does not error, it just leaves our mirror permanently wrong.
/// </summary>
public class PayMongoQrWebhookTests
{
    /// <summary>A real qr.paid resource, as PayMongo documents it.</summary>
    private static JsonElement Payload(
        string id = "qr_fcdd7b768c10e15570d34c22",
        long amount = 5000,
        string creditAccountNumber = "817797809438",
        string? metadataMerchantId = "org_03d4711d3f65a6189b624d79",
        string bareMerchantId = "03d4711d3f65a6189b624d79",
        string? transferId = "tr_01e65483cedaaa9b5c2aa248")
    {
        var metadata = transferId is null && metadataMerchantId is null
            ? "{}"
            : "{" + string.Join(",", new[]
              {
                  metadataMerchantId is null ? null : $"\"merchant_id\":\"{metadataMerchantId}\"",
                  transferId is null ? null : $"\"transfer_id\":\"{transferId}\"",
              }.Where(x => x is not null)) + "}";

        var json = $$"""
        {
          "id": "{{id}}",
          "transaction_amount": {{amount}},
          "transaction_currency": "PHP",
          "merchant_id": "{{bareMerchantId}}",
          "credit_account_number": "{{creditAccountNumber}}",
          "reference_label": "R1M",
          "metadata": {{metadata}}
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void The_amount_is_centavos()
    {
        // 5000 is ₱50.00. Reading it as pesos would credit the driver 100x what they paid.
        Assert.Equal(50.00m, PayMongoWebhooksController.ReadQrPayment(Payload(amount: 5000)).Amount);
    }

    /// <summary>
    /// The trap the whole design turns on. A BeeWallet QR is <b>static</b>: <c>id</c> is the same
    /// string for every payment that driver ever receives. Keying idempotency on it would credit
    /// the first top-up and silently swallow every one after.
    /// </summary>
    [Fact]
    public void The_idempotency_key_is_the_transfer_id_not_the_qr_id()
    {
        var payment = PayMongoWebhooksController.ReadQrPayment(Payload());

        Assert.Equal("tr_01e65483cedaaa9b5c2aa248", payment.IdempotencyKey);
        Assert.NotEqual("qr_fcdd7b768c10e15570d34c22", payment.IdempotencyKey);
    }

    [Fact]
    public void Two_payments_on_one_static_qr_read_as_two_distinct_keys()
    {
        var first = PayMongoWebhooksController.ReadQrPayment(Payload(transferId: "tr_first"));
        var second = PayMongoWebhooksController.ReadQrPayment(Payload(transferId: "tr_second"));

        // Same QR id in both payloads; only the transfer id separates them.
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void A_payload_with_no_transfer_id_yields_no_key()
    {
        // Deliberately unusable rather than falling back to the QR id, which would double-credit
        // on redelivery and drop every payment after the first.
        Assert.Null(PayMongoWebhooksController.ReadQrPayment(Payload(transferId: null)).IdempotencyKey);
    }

    [Fact]
    public void The_prefixed_merchant_id_from_metadata_wins()
    {
        // What we store on the wallet is the org_-prefixed form.
        Assert.Equal("org_03d4711d3f65a6189b624d79",
            PayMongoWebhooksController.ReadQrPayment(Payload()).AccountId);
    }

    [Fact]
    public void The_bare_top_level_merchant_id_gets_its_org_prefix_back()
    {
        // The top-level merchant_id omits the prefix. Passing it through unprefixed would match no
        // wallet, and the payment would be dropped as "not ours".
        var payment = PayMongoWebhooksController.ReadQrPayment(Payload(metadataMerchantId: null));

        Assert.Equal("org_03d4711d3f65a6189b624d79", payment.AccountId);
    }

    [Fact]
    public void The_credit_account_number_is_read_verbatim()
        => Assert.Equal("817797809438",
            PayMongoWebhooksController.ReadQrPayment(Payload()).CreditAccountNumber);
}
