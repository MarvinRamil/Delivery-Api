using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Phase 3: withdrawals leaving the driver's own PayMongo wallet (issue #91).
///
/// <para>The rule everything here defends: <b>never ask PayMongo for more than the wallet covers
/// including the fee.</b> PayMongo takes its fee from the source wallet, so a driver withdrawing
/// their whole balance is short by exactly the fee — and the rejection reads to them as a broken
/// app rather than an amount they can fix.</para>
/// </summary>
public class PayMongoWithdrawalServiceTests
{
    private static readonly Guid DriverId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();

    private PayMongoWithdrawalService Service(bool enabled = true, decimal fee = 10m) => new(
        _repo, _accounts,
        Options.Create(new DriverWalletOptions
        {
            PayMongoWithdrawalsEnabled = enabled,
            ExpectedWithdrawalFee = fee
        }),
        NullLogger<PayMongoWithdrawalService>.Instance);

    private DriverWallet Migrated()
    {
        var w = new DriverWallet(DriverId);
        w.LinkPayMongoAccount("org_a");
        w.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        w.UpdateBankDetails("1234567890", "GCash", "Juan Dela Cruz");
        _repo.Wallets.Add(w);
        return w;
    }

    private void RemoteBalance(decimal available) =>
        _accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoChildWallet("wallet_a", "org_a", "activated", "default",
                "285168654745", "Juan Dela Cruz", "ledger_1", available, available));

    private void PayoutSucceeds() =>
        _accounts.WithdrawFromChildAsync(Arg.Any<ChildWalletPayoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoInternalTransfer(
                "tr_out", "succeeded", ci.Arg<ChildWalletPayoutRequest>().Amount, 10m, null));

    // ── The fee headroom rule ──────────────────────────────────────────

    [Fact]
    public async Task Withdrawing_the_entire_balance_is_refused_with_the_real_maximum()
    {
        // Reproduces what we hit live: a ₱20 wallet cannot send ₱20, because ₱20 + ₱10 fee > ₱20.
        // Telling the driver the number they CAN withdraw is the difference between a fixable
        // message and "your withdrawal failed".
        var wallet = Migrated();
        RemoteBalance(20m);

        var result = await Service().WithdrawAsync(wallet, 20m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("10.00", result.Error);
        await _accounts.DidNotReceiveWithAnyArgs().WithdrawFromChildAsync(default!, default);
    }

    [Fact]
    public async Task Balance_minus_the_fee_succeeds()
    {
        var wallet = Migrated();
        RemoteBalance(500m);
        PayoutSucceeds();

        var result = await Service().WithdrawAsync(wallet, 490m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(WithdrawalStatus.Approved, result.Value!.Status);
        Assert.Equal("tr_out", result.Value.ProviderDisbursementId);
    }

    [Fact]
    public async Task A_balance_below_the_fee_says_so_plainly()
    {
        var wallet = Migrated();
        RemoteBalance(5m);

        var result = await Service().WithdrawAsync(wallet, 5m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        Assert.Contains("transfer fee", result.Error);
    }

    [Fact]
    public async Task Withdrawable_amount_excludes_the_fee()
    {
        var wallet = Migrated();
        RemoteBalance(1000m);

        var max = await Service().GetWithdrawableAsync(wallet);

        Assert.Equal(990m, max.Value);
    }

    [Fact]
    public async Task Withdrawable_never_goes_negative()
    {
        var wallet = Migrated();
        RemoteBalance(3m);

        var max = await Service().GetWithdrawableAsync(wallet);

        Assert.Equal(0m, max.Value);
    }

    // ── Balance source ─────────────────────────────────────────────────

    [Fact]
    public async Task Authorises_against_PayMongos_balance_not_our_mirror()
    {
        // The mirror exists to DETECT drift, not to authorise money movement. A mirror reading high
        // would let a driver request more than they hold, and the failure would land at PayMongo.
        var wallet = Migrated();
        wallet.AddEarning(5000m);   // mirror says 5000
        RemoteBalance(100m);        // PayMongo says 100

        var result = await Service().WithdrawAsync(wallet, 200m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceiveWithAnyArgs().WithdrawFromChildAsync(default!, default);
    }

    [Fact]
    public async Task An_unreadable_balance_blocks_rather_than_guesses()
    {
        var wallet = Migrated();
        _accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((PayMongoChildWallet?)null);

        var result = await Service().WithdrawAsync(wallet, 50m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceiveWithAnyArgs().WithdrawFromChildAsync(default!, default);
    }

    // ── Routing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sends_from_the_drivers_wallet_on_behalf_of_their_account()
    {
        // Without Account-Id the transfer debits the PLATFORM wallet - the driver would be paid out
        // of our money, and their own balance would never move.
        var wallet = Migrated();
        RemoteBalance(500m);
        PayoutSucceeds();

        await Service().WithdrawAsync(wallet, 100m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        await _accounts.Received(1).WithdrawFromChildAsync(
            Arg.Is<ChildWalletPayoutRequest>(r =>
                r.AccountId == "org_a" &&
                r.SourceAccountNumber == "285168654745" &&
                r.DestinationAccountNumber == "1234567890"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_bank_is_rejected_before_any_money_moves()
    {
        var wallet = Migrated();
        RemoteBalance(500m);

        var result = await Service().WithdrawAsync(wallet, 100m, "NOT_A_BANK", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        await _accounts.DidNotReceiveWithAnyArgs().WithdrawFromChildAsync(default!, default);
    }

    // ── Failure and replay ─────────────────────────────────────────────

    [Fact]
    public async Task A_rejected_transfer_is_recorded_as_failed()
    {
        var wallet = Migrated();
        RemoteBalance(500m);
        _accounts.WithdrawFromChildAsync(Arg.Any<ChildWalletPayoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_out", "failed", 100m, 10m,
                "validation failed for transfer_validator.source_account_balance: insufficient"));

        var result = await Service().WithdrawAsync(wallet, 100m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.False(result.IsSuccess);
        var recorded = Assert.Single(_repo.WithdrawalRequests);
        Assert.Equal(WithdrawalStatus.Failed, recorded.Status);
    }

    [Fact]
    public async Task Replaying_an_idempotency_key_returns_the_first_request()
    {
        var wallet = Migrated();
        RemoteBalance(500m);
        PayoutSucceeds();

        var first = await Service().WithdrawAsync(wallet, 100m, "GCASH", "1234567890", "Juan Dela Cruz", "key-1");
        var second = await Service().WithdrawAsync(wallet, 100m, "GCASH", "1234567890", "Juan Dela Cruz", "key-1");

        Assert.Equal(first.Value!.Id, second.Value!.Id);
        await _accounts.Received(1).WithdrawFromChildAsync(Arg.Any<ChildWalletPayoutRequest>(), Arg.Any<CancellationToken>());
    }

    // ── The flag ───────────────────────────────────────────────────────

    [Fact]
    public void Handles_only_migrated_drivers_with_the_flag_on()
    {
        var migrated = Migrated();
        var plain = new DriverWallet(Guid.NewGuid());

        Assert.True(Service(enabled: true).Handles(migrated));
        Assert.False(Service(enabled: false).Handles(migrated));
        Assert.False(Service(enabled: true).Handles(plain));
    }

    // ── The fee the driver actually paid ───────────────────────────────

    /// <summary>
    /// The fee has to be captured here or it is lost: both settle paths run later, from the stored
    /// withdrawal, with no transfer left to read it off. Without it they debit the amount alone and
    /// the mirror stays permanently high by the fee — which the balance reconciler cannot repair,
    /// since it only ever credits.
    /// </summary>
    [Fact]
    public async Task The_fee_PayMongo_charged_is_recorded_on_the_withdrawal()
    {
        // The live case: PHP 70 wallet, PHP 50 out, PHP 10 fee, PHP 10 left at PayMongo.
        var wallet = Migrated();
        RemoteBalance(70m);
        PayoutSucceeds();

        var result = await Service().WithdrawAsync(wallet, 50m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, result.Value.Fee);
        // What actually left the driver's wallet, and what the mirror must come down by.
        Assert.Equal(60m, result.Value.TotalDebitedFromSource);
    }

    /// <summary>
    /// The reported fee wins over the configured estimate. ExpectedWithdrawalFee is documented as
    /// an upper bound for validating the request, and observed fees have varied between channels.
    /// </summary>
    [Fact]
    public async Task The_reported_fee_wins_over_the_configured_estimate()
    {
        var wallet = Migrated();
        RemoteBalance(200m);
        _accounts.WithdrawFromChildAsync(Arg.Any<ChildWalletPayoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_out", "succeeded", 50m, 25m, null));

        var result = await Service().WithdrawAsync(wallet, 50m, "GCASH", "1234567890", "Juan Dela Cruz", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(25m, result.Value.Fee);   // not ExpectedWithdrawalFee, which is 10
    }

}

/// <summary>
/// The withdrawable-balance query the app uses to cap its amount field.
/// </summary>
public class WithdrawableBalanceQueryTests
{
    private static readonly Guid DriverId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();

    private BeeLogistics.Modules.Drivers.Application.Handlers.GetWithdrawableBalanceQueryHandler
        Handler(bool enabled)
    {
        var options = Options.Create(new DriverWalletOptions
        {
            PayMongoWithdrawalsEnabled = enabled,
            ExpectedWithdrawalFee = 10m
        });
        return new(_repo,
            new PayMongoWithdrawalService(_repo, _accounts, options,
                NullLogger<PayMongoWithdrawalService>.Instance),
            options);
    }

    [Fact]
    public async Task A_migrated_driver_sees_the_fee_deducted_from_what_they_can_withdraw()
    {
        var w = new DriverWallet(DriverId);
        w.LinkPayMongoAccount("org_a");
        w.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        _repo.Wallets.Add(w);
        _accounts.GetWalletAsync("org_a", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoChildWallet("wallet_a", "org_a", "activated", "default",
                "285168654745", "A B", "ledger_1", 1000m, 1000m));

        var result = await Handler(enabled: true).Handle(
            new BeeLogistics.Modules.Drivers.Application.Handlers.GetWithdrawableBalanceQuery(DriverId), default);

        Assert.Equal(1000m, result.Value!.Balance);
        Assert.Equal(990m, result.Value.Withdrawable);
        Assert.Equal(10m, result.Value.Fee);
        Assert.True(result.Value.FeePaidByDriver);
    }

    [Fact]
    public async Task An_unmigrated_driver_can_withdraw_their_whole_balance()
    {
        // On the original path the platform absorbs the fee, so balance and withdrawable are the
        // same number - the app must not start showing a deduction that does not apply to them.
        var w = new DriverWallet(DriverId);
        w.AddEarning(750m);
        _repo.Wallets.Add(w);

        var result = await Handler(enabled: true).Handle(
            new BeeLogistics.Modules.Drivers.Application.Handlers.GetWithdrawableBalanceQuery(DriverId), default);

        Assert.Equal(750m, result.Value!.Balance);
        Assert.Equal(750m, result.Value.Withdrawable);
        Assert.Equal(0m, result.Value.Fee);
        Assert.False(result.Value.FeePaidByDriver);
    }
}

/// <summary>
/// The flags must not be settable to a combination that loses money.
///
/// <para>With <c>EarningsPushEnabled</c> on and <c>WithdrawalsEnabled</c> off, a migrated driver's
/// earnings sit in their PayMongo wallet while the platform path would still reserve against the
/// local mirror and disburse from the <b>platform</b> wallet — paying them twice, once into their
/// own wallet and again out of ours.</para>
/// </summary>
public class MigratedDriverNeverUsesThePlatformPathTests
{
    [Fact]
    public void A_migrated_driver_is_not_handled_when_withdrawals_are_disabled()
    {
        // Handles() being false is exactly the state that used to fall through to the platform
        // path. The handler now refuses instead — this test pins the precondition that makes that
        // refusal necessary, so the two cannot drift apart.
        var repo = new FakeDriverWalletRepository();
        var accounts = Substitute.For<IPayMongoAccountsClient>();

        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");

        var disabled = new PayMongoWithdrawalService(repo, accounts,
            Options.Create(new DriverWalletOptions { PayMongoWithdrawalsEnabled = false }),
            NullLogger<PayMongoWithdrawalService>.Instance);

        Assert.True(wallet.UsesPayMongoWallet);
        Assert.False(disabled.Handles(wallet));
    }
}
