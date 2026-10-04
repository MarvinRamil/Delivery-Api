using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// The QR a driver scans to fund their own BeeWallet wallet (issue #93).
///
/// <para>
/// Replaces a checkout, which is a payment that only reaches a wallet on PayMongo's weekly
/// settlement run — so a top-up showed the platform as the merchant and we fronted the driver's
/// money until it landed. A QR Ph transfer settles in real time and credits their wallet directly.
/// </para>
/// </summary>
public class BeeWalletTopUpQrTests
{
    private static readonly Guid DriverId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private const string AccountNumber = "817797809438";

    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();

    private GetBeeWalletTopUpQrQueryHandler Handler() =>
        new(_repo, _accounts, NullLogger<GetBeeWalletTopUpQrQueryHandler>.Instance);

    private DriverWallet Migrated()
    {
        var w = new DriverWallet(DriverId);
        w.LinkPayMongoAccount("org_a");
        w.SetPayMongoWallet("wallet_a", AccountNumber, "ledger_1");
        _repo.Wallets.Add(w);
        return w;
    }

    private void QrCredits(string accountNumber) =>
        _accounts.GenerateWalletQrAsync(Arg.Any<GenerateWalletQrRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoWalletQr(
                "qr_1", "00020101021127590012com.p2pqrpay", "p2p", "static", "active",
                "CABONEGRO EDREN VON Tara", accountNumber, "org_a", null, null, null));

    [Fact]
    public async Task The_QR_is_generated_for_the_drivers_own_account()
    {
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountNumber, result.Value!.AccountNumber);
        await _accounts.Received(1).GenerateWalletQrAsync(
            Arg.Is<GenerateWalletQrRequest>(r =>
                r.OnBehalfOfAccountId == "org_a" &&
                r.Mode == WalletQrMode.P2P &&
                r.Type == WalletQrType.Static),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_QR_that_credits_someone_else_is_refused()
    {
        // The failure this guards against is silent: without Account-Id the endpoint returns the
        // PLATFORM's QR, which looks identical and would have the driver paying us instead of
        // themselves. Better to show nothing than the wrong QR.
        Migrated();
        QrCredits("721777442708");   // the platform's account number

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_driver_without_BeeWallet_is_told_to_finish_setup()
    {
        // Generating here would produce the platform's QR — exactly the confusion this replaces.
        var plain = new DriverWallet(DriverId);
        _repo.Wallets.Add(plain);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.Contains("setting up BeeWallet", result.Error);
        await _accounts.DidNotReceiveWithAnyArgs().GenerateWalletQrAsync(default!, default);
    }

    [Fact]
    public async Task A_static_QR_carries_no_expiry()
    {
        // It belongs to the driver, not to one payment, so it can be shown again tomorrow.
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.Null(result.Value!.ExpiresAt);
    }

    [Fact]
    public async Task A_provider_failure_does_not_surface_raw()
    {
        Migrated();
        _accounts.GenerateWalletQrAsync(Arg.Any<GenerateWalletQrRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PaymentGatewayException("PayMongo", 500, "internal_server_error", "QRPH invalid mode: "));

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("QRPH", result.Error);
    }

    [Fact]
    public async Task The_QR_is_returned_as_a_renderable_image()
    {
        // The app has no QR renderer, and the obvious one carries native code — adding it would
        // force a new dev/EAS build rather than a JavaScript-only update. Rendering here means the
        // app just shows an <Image>.
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        var image = result.Value!.QrImage;
        Assert.StartsWith("data:image/png;base64,", image);

        // Decode it: a data URI that is not actually a PNG renders as a broken image, which looks
        // like a backend outage to a driver.
        var bytes = Convert.FromBase64String(image["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);   // PNG magic
    }

    [Fact]
    public async Task The_raw_payload_is_returned_alongside_the_image()
    {
        // Kept so the app can offer copy-to-clipboard, and so a support conversation can compare
        // the payload against PayMongo's record without decoding a picture.
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.StartsWith("000201", result.Value!.QrString);   // EMV payload format indicator
    }

    // ── Fixing the amount in the code ──────────────────────────────────

    /// <summary>
    /// An amount makes the QR dynamic. The two differ in more than a field: a dynamic code carries
    /// the amount so the payer cannot mistype it, and expires; a static one is reusable forever.
    /// </summary>
    [Fact]
    public async Task An_amount_asks_PayMongo_for_a_dynamic_qr()
    {
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId, 250m), default);

        Assert.True(result.IsSuccess);
        await _accounts.Received(1).GenerateWalletQrAsync(
            Arg.Is<GenerateWalletQrRequest>(r => r.Type == WalletQrType.Dynamic && r.Amount == 250m),
            Arg.Any<CancellationToken>());
        // Echoed back so the app can show what the payer will be charged.
        Assert.Equal(250m, result.Value!.Amount);
    }

    [Fact]
    public async Task No_amount_still_asks_for_the_reusable_static_qr()
    {
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        await _accounts.Received(1).GenerateWalletQrAsync(
            Arg.Is<GenerateWalletQrRequest>(r => r.Type == WalletQrType.Static && r.Amount == null),
            Arg.Any<CancellationToken>());
        Assert.Null(result.Value!.Amount);
    }

    /// <summary>
    /// Rejected before the call: the client throws ArgumentException on a non-positive dynamic
    /// amount, which would reach the driver as a 500 rather than something they can act on.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task A_non_positive_amount_never_reaches_PayMongo(decimal amount)
    {
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId, amount), default);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceiveWithAnyArgs().GenerateWalletQrAsync(default!, default);
    }

    /// <summary>
    /// QR Ph carries the amount in the payload, so sub-centavo precision yields a code that either
    /// fails to parse or silently rounds - neither of which a driver could explain.
    /// </summary>
    [Fact]
    public async Task An_amount_finer_than_centavos_is_refused()
    {
        Migrated();
        QrCredits(AccountNumber);

        var result = await Handler().Handle(new GetBeeWalletTopUpQrQuery(DriverId, 10.005m), default);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceiveWithAnyArgs().GenerateWalletQrAsync(default!, default);
    }

}
