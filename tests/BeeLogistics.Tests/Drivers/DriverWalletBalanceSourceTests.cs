using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Where the personal balance on <c>GET /api/drivers/{id}/wallet</c> comes from (issue #101).
///
/// <para>
/// For a migrated driver the money is at PayMongo, and our column only learns about a QR top-up if
/// something reconciles it — which nothing reliably does, because <c>qr.paid</c> is emitted on the
/// child account where the parent's webhook never sees it. So the balance is read live, and the
/// stored figure becomes the fallback rather than the answer.
/// </para>
/// </summary>
public class DriverWalletBalanceSourceTests
{
    private static readonly Guid DriverId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private const string AccountNumber = "817797809438";

    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();

    private GetDriverWalletQueryHandler Handler() =>
        new(_repo, Options.Create(new DriverWalletOptions()), logger: null, accounts: _accounts);

    /// <summary>A driver whose balance lives in their own PayMongo child wallet.</summary>
    private DriverWallet Migrated(decimal storedBalance)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", AccountNumber, "ledger_1");
        if (storedBalance > 0) wallet.AddEarning(storedBalance);
        _repo.Wallets.Add(wallet);
        return wallet;
    }

    private void RemoteBalanceIs(decimal? available) =>
        _accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoChildWallet(
                "wallet_a", "org_a", "active", "default",
                AccountNumber, "Driver", "ledger_1", available, null));

    [Fact]
    public async Task A_migrated_wallet_reports_the_balance_PayMongo_holds()
    {
        // The stored figure is behind by a top-up nothing credited — the whole bug.
        Migrated(storedBalance: 1000m);
        RemoteBalanceIs(1500m);

        var result = await Handler().Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1500m, result.Value!.PersonalBalance);
    }

    [Fact]
    public async Task A_provider_failure_serves_the_stored_balance_rather_than_failing()
    {
        // A stale balance is what the driver sees today. An error screen because PayMongo was
        // unreachable would be worse than the bug being fixed.
        Migrated(storedBalance: 1000m);
        _accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PaymentGatewayException("PayMongo", 503, "unavailable", "upstream down"));

        var result = await Handler().Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1000m, result.Value!.PersonalBalance);
    }

    [Fact]
    public async Task A_response_without_a_balance_serves_the_stored_one()
    {
        // includeBalance can come back empty. Reading null as zero would tell a driver with money
        // that they have none.
        Migrated(storedBalance: 1000m);
        RemoteBalanceIs(null);

        var result = await Handler().Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.Equal(1000m, result.Value!.PersonalBalance);
    }

    [Fact]
    public async Task An_unmigrated_driver_is_never_read_from_PayMongo()
    {
        // Their balance is genuinely ours, and they must not gain a provider call on the app's
        // most-hit endpoint.
        var wallet = new DriverWallet(DriverId);
        wallet.AddEarning(750m);
        _repo.Wallets.Add(wallet);

        var result = await Handler().Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.Equal(750m, result.Value!.PersonalBalance);
        await _accounts.DidNotReceiveWithAnyArgs().GetWalletAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Without_a_PayMongo_client_the_stored_balance_is_served()
    {
        // The dependency is optional, so a host that does not register it still answers.
        Migrated(storedBalance: 1000m);

        var handler = new GetDriverWalletQueryHandler(
            _repo, Options.Create(new DriverWalletOptions()), logger: null, accounts: null);

        var result = await handler.Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.Equal(1000m, result.Value!.PersonalBalance);
    }

    [Fact]
    public async Task Only_the_personal_balance_comes_from_PayMongo()
    {
        // The Cash Wallet float is funded by checkout and never leaves us, and cash-job eligibility
        // is decided on that float — so neither may move because the personal balance did.
        var wallet = Migrated(storedBalance: 1000m);
        wallet.AddTopUp(500m);
        RemoteBalanceIs(1500m);

        var result = await Handler().Handle(new GetDriverWalletQuery(DriverId), default);

        Assert.Equal(1500m, result.Value!.PersonalBalance);
        Assert.Equal(500m, result.Value!.TopUpBalance);
    }
}
