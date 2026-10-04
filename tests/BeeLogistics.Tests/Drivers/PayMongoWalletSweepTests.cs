using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Phase 2 rollback (issue #91 §3.2). Turning the flag off is not a rollback on its own — once
/// earnings land in child wallets the money physically sits at PayMongo, and flipping the flag
/// would strand it. These tests exist so the recovery path is proven <b>before</b> the flag is ever
/// enabled.
/// </summary>
public class PayMongoWalletSweepTests
{
    private static readonly Guid DriverId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DriverWallet Migrated()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        return wallet;
    }

    private static (PayMongoWalletSweepService Service, FakeDriverWalletRepository Repo, IPayMongoAccountsClient Accounts)
        Create(DriverWallet? wallet, decimal remoteBalance)
    {
        var repo = new FakeDriverWalletRepository();
        if (wallet is not null) repo.Wallets.Add(wallet);

        var accounts = Substitute.For<IPayMongoAccountsClient>();
        accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoChildWallet("wallet_a", "org_a", "activated", "default",
                "285168654745", "A B", "ledger_1", remoteBalance, remoteBalance));

        return (new PayMongoWalletSweepService(repo, accounts, NullLogger<PayMongoWalletSweepService>.Instance),
                repo, accounts);
    }

    private static PayMongoInternalTransfer Ok(decimal amount) =>
        new("tr_1", "succeeded", amount, 0m, null);

    private static PayMongoInternalTransfer Failed() =>
        new("tr_1", "failed", 0m, 0m, "validation failed for transfer_validator.source_account_balance: insufficient");

    [Fact]
    public async Task Sweeps_the_remote_balance_and_returns_the_driver_to_the_local_path()
    {
        var wallet = Migrated();
        var (service, _, accounts) = Create(wallet, remoteBalance: 1250.75m);
        accounts.SweepFromChildAsync("org_a", "285168654745", Arg.Any<string>(), 1250.75m,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Ok(1250.75m));

        var result = await service.SweepAsync(DriverId, unlink: true);

        Assert.True(result.Swept);
        Assert.Equal(1250.75m, result.Amount);
        Assert.Null(wallet.PayMongoAccountId);
        Assert.False(wallet.UsesPayMongoWallet);
    }

    [Fact]
    public async Task Sweeps_PayMongos_balance_not_our_mirror()
    {
        // The mirror can drift. Sweeping its figure would fail outright if it read high, and would
        // silently leave money behind if it read low - which is the worse of the two, because
        // nothing would flag it.
        var wallet = Migrated();
        wallet.AddEarning(900m); // mirror says 900
        var (service, _, accounts) = Create(wallet, remoteBalance: 1000m); // PayMongo says 1000
        accounts.SweepFromChildAsync("org_a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Ok(1000m));

        var result = await service.SweepAsync(DriverId, unlink: true);

        Assert.Equal(1000m, result.Amount);
        await accounts.Received(1).SweepFromChildAsync("org_a", Arg.Any<string>(), Arg.Any<string>(),
            1000m, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_sweep_leaves_the_link_intact()
    {
        // Unlinking after a failed sweep would hide a wallet still holding the driver's money
        // behind a flag nobody is watching any more. The link is the only record of where it is.
        var wallet = Migrated();
        var (service, _, accounts) = Create(wallet, remoteBalance: 500m);
        accounts.SweepFromChildAsync("org_a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Failed());

        var result = await service.SweepAsync(DriverId, unlink: true);

        Assert.False(result.Swept);
        Assert.Contains("insufficient", result.Reason);
        Assert.Equal("org_a", wallet.PayMongoAccountId);
        Assert.True(wallet.UsesPayMongoWallet);
    }

    [Fact]
    public async Task An_empty_wallet_unlinks_without_a_transfer()
    {
        var wallet = Migrated();
        var (service, _, accounts) = Create(wallet, remoteBalance: 0m);

        var result = await service.SweepAsync(DriverId, unlink: true);

        Assert.False(result.Swept);
        Assert.Null(wallet.PayMongoAccountId);
        await accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }

    [Fact]
    public async Task Unlink_false_recovers_the_money_but_keeps_the_account()
    {
        // For pausing rather than abandoning the migration.
        var wallet = Migrated();
        var (service, _, accounts) = Create(wallet, remoteBalance: 300m);
        accounts.SweepFromChildAsync("org_a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Ok(300m));

        var result = await service.SweepAsync(DriverId, unlink: false);

        Assert.True(result.Swept);
        Assert.Equal("org_a", wallet.PayMongoAccountId);
        Assert.True(wallet.UsesPayMongoWallet);
    }

    [Fact]
    public async Task An_unmigrated_driver_is_a_no_op()
    {
        var plain = new DriverWallet(DriverId);
        var (service, _, accounts) = Create(plain, remoteBalance: 0m);

        var result = await service.SweepAsync(DriverId, unlink: true);

        Assert.False(result.Swept);
        await accounts.DidNotReceiveWithAnyArgs().SweepFromChildAsync(
            default!, default!, default!, default, default!, default!, default);
    }
}
