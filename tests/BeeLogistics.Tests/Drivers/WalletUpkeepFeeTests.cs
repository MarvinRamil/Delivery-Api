using BeeLogistics.Modules.Drivers.Application.Services;
using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Monthly PayMongo wallet upkeep (issue #91). The invariant: a driver is charged <b>at most once
/// per calendar month</b>, no matter how often the job runs, retries or overlaps with itself.
/// </summary>
public class WalletUpkeepFeeTests
{
    private static readonly DateTimeOffset September = new(2026, 9, 14, 3, 0, 0, TimeSpan.Zero);

    private static DriverWallet Activated(decimal float_ = 970m)
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        if (float_ != 0) wallet.AddTopUp(float_);
        wallet.LinkPayMongoAccount($"org_{Guid.NewGuid():N}");
        wallet.SetPayMongoWallet($"wallet_{Guid.NewGuid():N}", "285168654745", "ledger_1");
        return WithId(wallet);
    }

    /// <summary>
    /// Gives the wallet a distinct id, as the database would.
    /// <para>
    /// Entity.Id has a protected setter and stays Guid.Empty until a row is written, so wallets
    /// built in memory all share one id. That matters here specifically: the monthly fee key is the
    /// same string for every driver, and it is <c>WalletId</c> in the (WalletId, Type,
    /// ProviderPaymentId) unique index that keeps one driver's charge from blocking another's.
    /// Leaving the ids empty makes the fixture collide in a way production never would, and would
    /// hide a genuine regression behind a fixture artefact.
    /// </para>
    /// </summary>
    private static DriverWallet WithId(DriverWallet wallet)
    {
        typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(BeeLogistics.Shared.Abstractions.Entity.Id))!
            .SetValue(wallet, Guid.NewGuid());
        return wallet;
    }

    private static (WalletUpkeepFeeService Service, FakeDriverWalletRepository Repo) Create(
        DateTimeOffset now, bool feesEnabled = true, decimal fee = 15m)
    {
        var repo = new FakeDriverWalletRepository();
        var clock = new FakeTimeProvider(now);
        var service = new WalletUpkeepFeeService(
            repo,
            Options.Create(new DriverWalletOptions
            {
                AccountFeesEnabled = feesEnabled,
                MonthlyWalletFeeAmount = fee,
                DefaultTopUpNegativeLimit = -500m
            }),
            clock,
            NullLogger<WalletUpkeepFeeService>.Instance);
        return (service, repo);
    }

    [Fact]
    public async Task Charges_each_activated_wallet_once()
    {
        var (service, repo) = Create(September);
        repo.Wallets.Add(Activated());
        repo.Wallets.Add(Activated());

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Equal(2, repo.Transactions.Count(t => t.Type == WalletTransactionType.AccountFee));
        Assert.All(repo.Wallets, w => Assert.Equal(955m, w.TopUpBalance));
        Assert.All(repo.Transactions, t => Assert.Equal("acctfee-upkeep-2026-09", t.ProviderPaymentId));
    }

    [Fact]
    public async Task Running_twice_in_the_same_month_does_not_double_charge()
    {
        // The job runs daily so it can pick up wallets activated mid-month and recover from an
        // outage. That only works if a repeat run within the month is a no-op.
        var (service, repo) = Create(September);
        var wallet = Activated();
        repo.Wallets.Add(wallet);

        await service.ChargeMonthlyUpkeepAsync();
        await service.ChargeMonthlyUpkeepAsync();
        await service.ChargeMonthlyUpkeepAsync();

        Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.AccountFee);
        Assert.Equal(955m, wallet.TopUpBalance);
    }

    [Fact]
    public async Task A_new_month_charges_again()
    {
        var wallet = Activated();

        var (september, repo) = Create(September);
        repo.Wallets.Add(wallet);
        await september.ChargeMonthlyUpkeepAsync();

        var october = new WalletUpkeepFeeService(
            repo,
            Options.Create(new DriverWalletOptions
            {
                AccountFeesEnabled = true,
                MonthlyWalletFeeAmount = 15m,
                DefaultTopUpNegativeLimit = -500m
            }),
            new FakeTimeProvider(September.AddMonths(1)),
            NullLogger<WalletUpkeepFeeService>.Instance);
        await october.ChargeMonthlyUpkeepAsync();

        Assert.Equal(2, repo.Transactions.Count(t => t.Type == WalletTransactionType.AccountFee));
        Assert.Equal(940m, wallet.TopUpBalance);
    }

    [Fact]
    public async Task Unlinked_wallets_are_never_charged()
    {
        // A driver who is not on the child-wallet path costs us nothing at PayMongo, so charging
        // them upkeep would be taking money for a service they do not have.
        var (service, repo) = Create(September);
        var plain = WithId(new DriverWallet(Guid.NewGuid()));
        plain.AddTopUp(1000m);
        repo.Wallets.Add(plain);

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Empty(repo.Transactions);
        Assert.Equal(1000m, plain.TopUpBalance);
    }

    [Fact]
    public async Task Charge_is_allowed_to_take_the_float_negative()
    {
        // Skipping drivers who cannot pay would forgive the fee for exactly the drivers costing us
        // money. The cash-job block already forces a top-up before they can earn again.
        var (service, repo) = Create(September);
        var wallet = Activated(float_: 10m);
        repo.Wallets.Add(wallet);

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Equal(-5m, wallet.TopUpBalance);
        Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.AccountFee);
    }

    [Fact]
    public async Task Float_past_its_floor_is_skipped_rather_than_pushed_deeper()
    {
        var (service, repo) = Create(September);
        var wallet = Activated(float_: 0m);
        wallet.AdjustTopUpForCashDeficit(-495m); // -495; another 15 would breach -500
        repo.Wallets.Add(wallet);

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Equal(-495m, wallet.TopUpBalance);
        Assert.Empty(repo.Transactions.Where(t => t.Type == WalletTransactionType.AccountFee));
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest_of_the_fleet()
    {
        var (service, repo) = Create(September);
        var broke = Activated(float_: 0m);
        broke.AdjustTopUpForCashDeficit(-495m);
        var healthy = Activated();
        repo.Wallets.Add(broke);
        repo.Wallets.Add(healthy);

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Equal(955m, healthy.TopUpBalance);
        Assert.Single(repo.Transactions, t => t.Type == WalletTransactionType.AccountFee);
    }

    [Fact]
    public async Task Disabled_flag_charges_nobody()
    {
        var (service, repo) = Create(September, feesEnabled: false);
        var wallet = Activated();
        repo.Wallets.Add(wallet);

        await service.ChargeMonthlyUpkeepAsync();

        Assert.Empty(repo.Transactions);
        Assert.Equal(970m, wallet.TopUpBalance);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

/// <summary>
/// The sign convention for wallet transactions.
///
/// <para>Amounts are stored <b>positive</b>; <c>WalletTransactionType</c> carries the direction.
/// This is not cosmetic: <c>GetCalculatedPersonalBalanceAsync</c> sums by type, adding credits and
/// subtracting debits, so a negative stored amount would be double-negated. <c>AccountFee</c> was
/// briefly the only type that broke this, which is exactly the sort of drift worth pinning.</para>
/// </summary>
public class WalletTransactionSignConventionTests
{
    [Fact]
    public void Fee_rows_are_stored_positive_like_every_other_debit()
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.AddTopUp(100m);

        // The same shape the fee paths construct.
        var fee = new WalletTransaction(
            wallet.Id, WalletTransactionType.AccountFee, WalletBucket.TopUp,
            15m, WalletTransactionStatus.Completed, "Wallet upkeep 2026-09",
            providerPaymentId: "acctfee-upkeep-2026-09");

        var debit = new WalletTransaction(
            wallet.Id, WalletTransactionType.CashSettlementDebit, WalletBucket.TopUp,
            15m, WalletTransactionStatus.Completed, "Cash settlement");

        // A fee row and the pre-existing debit type it sits alongside must agree in sign, or any
        // report or reconciliation that sums the bucket gets one of them backwards.
        Assert.True(fee.Amount > 0);
        Assert.Equal(debit.Amount, fee.Amount);
    }

    [Fact]
    public void The_balance_moves_down_even_though_the_row_is_positive()
    {
        // Direction lives in the domain method, not the row's sign.
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.AddTopUp(100m);

        wallet.ChargeAccountFee(15m, allowedNegativeLimit: -500m);

        Assert.Equal(85m, wallet.TopUpBalance);
    }
}
