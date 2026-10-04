using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Crediting the local mirror from a PayMongo <c>qr.paid</c> webhook (issue #93).
/// <para>
/// The invariant: the mirror ends up holding <b>exactly</b> what PayMongo holds — every real
/// payment credited once, no payment credited twice, and no payment silently dropped.
/// </para>
/// </summary>
public class BeeWalletQrTopUpTests
{
    private const string AccountNumber = "817797809438";
    private const string AccountId = "org_03d4711d3f65a6189b624d79";

    private static DriverWallet LinkedWallet()
    {
        var wallet = new DriverWallet(Guid.NewGuid());
        wallet.LinkPayMongoAccount(AccountId);
        wallet.SetPayMongoWallet($"wallet_{Guid.NewGuid():N}", AccountNumber, "ledger_1");

        // Entity.Id stays Guid.Empty until a row is written, so wallets built in memory share one
        // id — and WalletId is what keeps one driver's payment from colliding with another's in
        // the (WalletId, Type, ProviderPaymentId) unique index.
        typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(BeeLogistics.Shared.Abstractions.Entity.Id))!
            .SetValue(wallet, Guid.NewGuid());
        return wallet;
    }

    private static (CreditBeeWalletTopUpCommandHandler Handler, FakeDriverWalletRepository Repo) Create()
    {
        var repo = new FakeDriverWalletRepository();
        return (new CreditBeeWalletTopUpCommandHandler(repo, NullLogger<CreditBeeWalletTopUpCommandHandler>.Instance), repo);
    }

    [Fact]
    public async Task A_paid_qr_credits_BeeWallet_and_leaves_the_cash_wallet_alone()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        var result = await handler.Handle(
            new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 500m, "tr_one"), default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal(500m, wallet.Balance);

        // The Cash Wallet float is funded through checkout, not through a BeeWallet QR. Crediting it
        // here would hand the driver COD capacity they never paid for.
        Assert.Equal(0m, wallet.TopUpBalance);

        var tx = Assert.Single(repo.Transactions);
        Assert.Equal(WalletTransactionType.TopUp, tx.Type);
        Assert.Equal(WalletBucket.Personal, tx.Bucket);
        Assert.Equal(WalletTransactionStatus.Completed, tx.Status);
        Assert.Equal("tr_one", tx.ProviderPaymentId);
    }

    [Fact]
    public async Task A_redelivered_webhook_credits_nothing()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        var cmd = new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 500m, "tr_one");
        await handler.Handle(cmd, default);
        var second = await handler.Handle(cmd, default);

        Assert.True(second.IsSuccess);
        Assert.False(second.Value);
        Assert.Equal(500m, wallet.Balance);
        Assert.Single(repo.Transactions);
    }

    /// <summary>
    /// The trap this whole design turns on. A BeeWallet QR is <b>static</b>: one QR id serves every
    /// payment the driver ever receives. Keying idempotency on the QR id instead of the per-payment
    /// transfer id would credit the driver's first top-up and silently swallow every one after it.
    /// </summary>
    [Fact]
    public async Task Two_payments_on_the_same_static_qr_both_credit()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        await handler.Handle(new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 500m, "tr_one"), default);
        var second = await handler.Handle(
            new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 250m, "tr_two"), default);

        Assert.True(second.Value);
        Assert.Equal(750m, wallet.Balance);
        Assert.Equal(2, repo.Transactions.Count);
    }

    [Fact]
    public async Task An_unlinked_account_is_acknowledged_without_crediting_anyone()
    {
        var (handler, repo) = Create();
        repo.Wallets.Add(LinkedWallet());

        // A QR paid on the platform's own wallet, not a driver's.
        var result = await handler.Handle(
            new CreditBeeWalletTopUpCommand("999999999999", "org_someone_else", 500m, "tr_x"), default);

        Assert.True(result.IsSuccess);   // acknowledged, so PayMongo stops redelivering
        Assert.False(result.Value);
        Assert.Empty(repo.Transactions);
    }

    /// <summary>
    /// P2M payloads carry an empty <c>credit_account_number</c>, so the account id has to be able
    /// to carry the match on its own.
    /// </summary>
    [Fact]
    public async Task An_empty_credit_account_number_falls_back_to_the_account_id()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        var result = await handler.Handle(
            new CreditBeeWalletTopUpCommand("", AccountId, 120m, "tr_fallback"), default);

        Assert.True(result.Value);
        Assert.Equal(120m, wallet.Balance);
    }

    [Fact]
    public async Task A_payment_without_an_idempotency_key_is_refused()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        var result = await handler.Handle(
            new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 500m, ""), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(0m, wallet.Balance);
        Assert.Empty(repo.Transactions);
    }

    [Fact]
    public async Task A_non_positive_amount_is_refused()
    {
        var (handler, repo) = Create();
        var wallet = LinkedWallet();
        repo.Wallets.Add(wallet);

        var result = await handler.Handle(
            new CreditBeeWalletTopUpCommand(AccountNumber, AccountId, 0m, "tr_zero"), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(0m, wallet.Balance);
    }
}
