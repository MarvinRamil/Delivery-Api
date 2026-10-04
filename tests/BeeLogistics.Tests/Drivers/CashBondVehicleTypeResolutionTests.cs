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
/// Where the cashbond reads a driver's vehicle type from.
///
/// <para>The status query originally read <c>DriverApplications.VehicleType</c> alone. Most
/// drivers have no application row — 43 of 46 driver wallets on dev — so it returned a null
/// <c>AmountDue</c>, and the app hides the whole cashbond card on that null rather than showing
/// an error. Both sides failed silently, and because the dispatch gate added in the same change
/// requires <c>CashBondBalance &gt; 0</c>, a driver who could never see the card could never be
/// offered a cash job either.</para>
/// </summary>
public class CashBondVehicleTypeResolutionTests
{
    private readonly FakeDriverWalletRepository _walletRepo = new();
    private readonly IDriverApplicationRepository _applicationRepo = Substitute.For<IDriverApplicationRepository>();
    private readonly IDriverCashBondConfigRepository _configRepo = Substitute.For<IDriverCashBondConfigRepository>();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private static readonly Guid DriverId = Guid.NewGuid();

    private GetDriverCashBondStatusQueryHandler StatusHandler()
        => new(_walletRepo, _applicationRepo, _configRepo, _mediator);

    /// <summary>A wallet on the PayMongo path, with no application row — the common case.</summary>
    private DriverWallet Wallet()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "817797809438", "ledger_1");
        _walletRepo.Wallets.Add(wallet);
        return wallet;
    }

    private void ConfiguredAmount(string vehicleType, decimal amount) =>
        _configRepo.GetByVehicleTypeAsync(vehicleType, Arg.Any<CancellationToken>())
            .Returns(new DriverCashBondConfig(vehicleType, amount));

    private void IdentitySays(string? vehicleType) =>
        _mediator.Send(Arg.Any<GetUserVehicleTypeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok<string?>(vehicleType));

    private void IdentityHasNoUser() =>
        _mediator.Send(Arg.Any<GetUserVehicleTypeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.NotFound<string?>("User not found"));

    private void ApplicationSays(string vehicleType) =>
        _applicationRepo.GetByUserIdAsync(DriverId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new DriverApplication(
                "Juan Dela Cruz", "juan@example.com", "0917", "fb.com/juan",
                "license.pdf", "clearance.pdf", "orcr.pdf", "ltfrb.pdf", "insurance.pdf",
                userId: DriverId.ToString(), vehicleType: vehicleType));

    // ── The reported bug ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_driver_with_no_application_row_still_gets_an_amount_from_their_identity_user()
    {
        Wallet();
        IdentitySays("Motorcycle");
        ConfiguredAmount("Motorcycle", 10m);

        var result = await StatusHandler().Handle(new GetDriverCashBondStatusQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Motorcycle", result.Value!.VehicleType);
        // Null here is what hid the card: the app gates the whole section on `amountDue != null`.
        Assert.Equal(10m, result.Value.AmountDue);
        Assert.False(result.Value.Paid);
    }

    [Fact]
    public async Task The_application_wins_when_it_has_one_so_onboarding_stays_authoritative()
    {
        Wallet();
        ApplicationSays("L300");
        IdentitySays("Motorcycle");
        ConfiguredAmount("L300", 15m);
        ConfiguredAmount("Motorcycle", 10m);

        var result = await StatusHandler().Handle(new GetDriverCashBondStatusQuery(DriverId), default);

        Assert.Equal("L300", result.Value!.VehicleType);
        Assert.Equal(15m, result.Value.AmountDue);
        await _mediator.DidNotReceive().Send(Arg.Any<GetUserVehicleTypeQuery>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A blank application field is the same as no application at all. Without this the fallback
    /// would be skipped for a half-filled row, which is the shape a resumed onboarding leaves.
    /// </summary>
    [Fact]
    public async Task A_blank_vehicle_type_on_the_application_still_falls_back()
    {
        Wallet();
        ApplicationSays("   ");
        IdentitySays("Motorcycle");
        ConfiguredAmount("Motorcycle", 10m);

        var result = await StatusHandler().Handle(new GetDriverCashBondStatusQuery(DriverId), default);

        Assert.Equal(10m, result.Value!.AmountDue);
    }

    // ── Neither source knows ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_on_file_anywhere_reports_no_amount_rather_than_failing()
    {
        Wallet();
        IdentitySays(null);

        var result = await StatusHandler().Handle(new GetDriverCashBondStatusQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.VehicleType);
        Assert.Null(result.Value.AmountDue);
    }

    /// <summary>
    /// A failed lookup must not be read as "no vehicle type" in a way that throws: the status
    /// endpoint is polled by the profile screen, so an Identity hiccup should degrade to a hidden
    /// card, never to a 500.
    /// </summary>
    [Fact]
    public async Task An_identity_lookup_that_fails_degrades_to_no_amount()
    {
        Wallet();
        IdentityHasNoUser();

        var result = await StatusHandler().Handle(new GetDriverCashBondStatusQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.AmountDue);
    }

    // ── The QR path resolves the same way ──────────────────────────────────────────────

    [Fact]
    public async Task Issuing_a_qr_works_for_a_driver_whose_vehicle_type_is_only_on_their_identity_user()
    {
        Wallet();
        IdentitySays("Motorcycle");
        ConfiguredAmount("Motorcycle", 10m);
        _accounts.GenerateWalletQrAsync(Arg.Any<GenerateWalletQrRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoWalletQr(
                "qr_1", "0002010102", "p2m", "dynamic", "active", "Bee", null, "org_platform",
                ci.Arg<GenerateWalletQrRequest>().Amount, ci.Arg<GenerateWalletQrRequest>().ReferenceLabel,
                DateTime.UtcNow.AddMinutes(30)));

        var handler = new CreateCashBondQrCommandHandler(
            _walletRepo, _applicationRepo, _configRepo, _mediator, _accounts);

        var result = await handler.Handle(new CreateCashBondQrCommand(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, result.Value!.Amount);
    }
}
