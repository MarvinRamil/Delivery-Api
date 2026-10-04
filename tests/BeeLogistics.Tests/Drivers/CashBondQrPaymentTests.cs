using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MediatR;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Paying the cashbond by QR into the <b>platform</b> wallet.
///
/// <para>The first implementation swept the driver's own child wallet, which had the dependency
/// backwards: the cashbond is due before a driver can be offered bookings, which is before
/// BeeWallet onboarding, so there is no child wallet to sweep from yet. It also could not settle
/// reliably — child-account webhooks never reach us — which is how three pending sweeps stacked up
/// and took ₱30 for a ₱10 cashbond on 2026-09-01.</para>
/// </summary>
public class CashBondQrPaymentTests
{
    private readonly FakeDriverWalletRepository _walletRepo = new();
    private readonly IDriverApplicationRepository _applicationRepo = Substitute.For<IDriverApplicationRepository>();
    private readonly IDriverCashBondConfigRepository _configRepo = Substitute.For<IDriverCashBondConfigRepository>();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private static readonly Guid DriverId = Guid.NewGuid();

    public CashBondQrPaymentTests()
    {
        _mediator.Send(Arg.Any<GetUserVehicleTypeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok<string?>("Motorcycle"));
        _configRepo.GetByVehicleTypeAsync("Motorcycle", Arg.Any<CancellationToken>())
            .Returns(new DriverCashBondConfig("Motorcycle", 10m));
        QrIssued("qr_1");
    }

    private void QrIssued(string qrId) =>
        _accounts.GenerateWalletQrAsync(Arg.Any<GenerateWalletQrRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoWalletQr(
                qrId, "0002010102", "p2m", "dynamic", "active", "Bee", null, "org_platform",
                ci.Arg<GenerateWalletQrRequest>().Amount, ci.Arg<GenerateWalletQrRequest>().ReferenceLabel,
                DateTime.UtcNow.AddMinutes(30)));

    private CreateCashBondQrCommandHandler QrHandler()
        => new(_walletRepo, _applicationRepo, _configRepo, _mediator, _accounts);

    private SettleCashBondQrPaymentCommandHandler SettleHandler() => new(_walletRepo);

    // ── Issuing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the rework: a driver with no BeeWallet at all can still pay. The old
    /// handler refused these with "Your PayMongo wallet must be activated".
    /// </summary>
    [Fact]
    public async Task A_driver_with_no_wallet_row_yet_can_still_get_a_cashbond_qr()
    {
        var result = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, result.Value!.Amount);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.QrString));
        // Rendered server-side: the app has no native QR library, by design.
        Assert.StartsWith("data:image/png;base64,", result.Value.QrImage);
        Assert.Single(_walletRepo.Wallets);
    }

    /// <summary>
    /// Null OnBehalfOfAccountId is what makes it the platform's QR. Passing the driver's account id
    /// would credit their own child wallet — the money would never reach us, and the driver would
    /// have paid themselves.
    /// </summary>
    /// <remarks>
    /// The driver here is deliberately given a linked child account. Without one this test is
    /// vacuous: a fresh wallet's PayMongoAccountId is already null, so passing it instead of null
    /// would assert exactly the same thing and the mutation would go unnoticed. It is also a real
    /// case — a driver who has onboarded to BeeWallet and is paying a re-issued cashbond.
    /// </remarks>
    [Fact]
    public async Task The_qr_credits_the_platform_not_the_driver()
    {
        var onboarded = new DriverWallet(DriverId);
        onboarded.LinkPayMongoAccount("org_driver");
        onboarded.SetPayMongoWallet("wallet_driver", "817797809438", "ledger_1");
        _walletRepo.Wallets.Add(onboarded);

        await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        await _accounts.Received(1).GenerateWalletQrAsync(
            Arg.Is<GenerateWalletQrRequest>(r =>
                r.OnBehalfOfAccountId == null
                && r.Mode == WalletQrMode.P2P
                && r.Type == WalletQrType.Dynamic
                && r.Amount == 10m),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Dynamic, so the amount is fixed in the code the driver scans. A static QR would let them
    /// type any amount and still have it arrive as a cashbond payment.
    /// </summary>
    [Fact]
    public async Task The_amount_is_fixed_into_the_code()
    {
        var result = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        Assert.Equal(10m, result.Value!.Amount);
        Assert.StartsWith(CreateCashBondQrCommandHandler.ReferencePrefix, result.Value.ReferenceLabel);
        Assert.NotNull(result.Value.ExpiresAt);
    }

    /// <summary>
    /// Asking twice must not open a second debt. Two live codes for one cashbond means a driver who
    /// scans the older one pays against a transaction nothing is watching.
    /// </summary>
    [Fact]
    public async Task Asking_twice_re_issues_the_same_transaction_rather_than_opening_a_second()
    {
        var first = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);
        var second = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        Assert.Equal(first.Value!.ReferenceLabel, second.Value!.ReferenceLabel);
        Assert.Single(_walletRepo.Transactions.Where(t => t.Type == WalletTransactionType.CashBondPayment));
    }

    [Fact]
    public async Task An_already_paid_cashbond_is_not_re_issued()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.MarkCashBondPaid(10m);
        _walletRepo.Wallets.Add(wallet);

        var result = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceive().GenerateWalletQrAsync(
            Arg.Any<GenerateWalletQrRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Settling ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Paying_the_qr_marks_the_cashbond_paid()
    {
        var issued = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        var settled = await SettleHandler().Handle(
            new SettleCashBondQrPaymentCommand(issued.Value!.ReferenceLabel, 10m, "tr_1"), default);

        Assert.True(settled.IsSuccess);
        var wallet = _walletRepo.Wallets.Single();
        Assert.True(wallet.HasPaidCashBond);
        Assert.Equal(10m, wallet.CashBondBalance);
        Assert.Equal(WalletTransactionStatus.Completed,
            _walletRepo.Transactions.Single(t => t.Type == WalletTransactionType.CashBondPayment).Status);
    }

    /// <summary>PayMongo retries until it gets a 2xx, so a redelivery is the normal case.</summary>
    [Fact]
    public async Task A_redelivered_webhook_does_not_pay_it_twice()
    {
        var issued = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);
        var reference = issued.Value!.ReferenceLabel;

        await SettleHandler().Handle(new SettleCashBondQrPaymentCommand(reference, 10m, "tr_1"), default);
        var again = await SettleHandler().Handle(new SettleCashBondQrPaymentCommand(reference, 10m, "tr_1"), default);

        Assert.True(again.IsSuccess);
        Assert.Equal(10m, _walletRepo.Wallets.Single().CashBondBalance);
    }

    [Fact]
    public async Task A_short_payment_does_not_settle_the_cashbond()
    {
        var issued = await QrHandler().Handle(new CreateCashBondQrCommand(DriverId), default);

        var settled = await SettleHandler().Handle(
            new SettleCashBondQrPaymentCommand(issued.Value!.ReferenceLabel, 4m, "tr_1"), default);

        Assert.False(settled.IsSuccess);
        Assert.False(_walletRepo.Wallets.Single().HasPaidCashBond);
    }

    /// <summary>A BeeWallet top-up QR carries no reference label and must not be read as a cashbond.</summary>
    [Fact]
    public async Task A_payment_that_is_not_a_cashbond_reference_is_refused()
    {
        var settled = await SettleHandler().Handle(
            new SettleCashBondQrPaymentCommand("topup-whatever", 10m, "tr_1"), default);

        Assert.False(settled.IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cashbond-not-a-guid")]
    [InlineData("beewallet-topup")]
    [InlineData("CB")]                          // prefix alone
    [InlineData("CB0000000000000000000")]       // one short
    [InlineData("CB000000000000000000000")]     // one long
    [InlineData("CB00000000000000000+0/")]      // outside the base62 alphabet
    public void Only_a_well_formed_cashbond_reference_parses(string? label)
        => Assert.Null(CreateCashBondQrCommandHandler.TransactionIdFrom(label));

    /// <summary>
    /// The failure that made this encoding necessary: PayMongo rejects QR generation outright with
    /// a 500 when the reference label exceeds EMV MPM's 25-character cap for field 62/05. The
    /// original "cashbond-{id:N}" was 41 characters and could never generate a QR at all.
    /// </summary>
    [Fact]
    public void A_reference_fits_inside_the_emv_reference_label_limit()
    {
        for (var i = 0; i < 200; i++)
        {
            var label = CreateCashBondQrCommandHandler.ReferenceFor(Guid.NewGuid());
            Assert.True(label.Length <= CreateCashBondQrCommandHandler.MaxReferenceLabelLength,
                $"'{label}' is {label.Length} characters");
        }
    }

    /// <summary>Alphanumeric only — EMV fields are not the place to discover a charset limit.</summary>
    [Fact]
    public void A_reference_uses_no_characters_beyond_letters_and_digits()
    {
        var label = CreateCashBondQrCommandHandler.ReferenceFor(Guid.NewGuid());
        Assert.All(label, c => Assert.True(char.IsLetterOrDigit(c), $"'{c}' is not alphanumeric"));
    }

    [Fact]
    public void A_reference_round_trips_to_its_transaction_id()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = Guid.NewGuid();
            Assert.Equal(id, CreateCashBondQrCommandHandler.TransactionIdFrom(
                CreateCashBondQrCommandHandler.ReferenceFor(id)));
        }
    }

    /// <summary>
    /// The edges the fixed-width padding exists for. An id that encodes to leading zeros would
    /// otherwise come back short and decode to a different value.
    /// </summary>
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("01a05de3-c9a6-7d22-a591-3ae6967d66b3")]
    public void Boundary_ids_round_trip_too(string raw)
    {
        var id = Guid.Parse(raw);
        var label = CreateCashBondQrCommandHandler.ReferenceFor(id);

        Assert.True(label.Length <= CreateCashBondQrCommandHandler.MaxReferenceLabelLength);
        Assert.Equal(id, CreateCashBondQrCommandHandler.TransactionIdFrom(label));
    }
}
