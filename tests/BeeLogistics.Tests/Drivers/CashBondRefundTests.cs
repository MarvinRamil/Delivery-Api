using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Tests.Fakes;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Admin-triggered release of a driver's cashbond back to their own child wallet on offboarding
/// (issue #103) — the reverse of <see cref="CashBondPaymentTests"/>.
/// </summary>
public class CashBondRefundTests
{
    private readonly FakeDriverWalletRepository _walletRepo = new();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();
    private static readonly Guid DriverId = Guid.NewGuid();

    private RefundCashBondCommandHandler Handler() => new(_walletRepo, _accounts);

    private DriverWallet PaidWallet(decimal amount = 1000m)
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "817797809438", "ledger_1");
        wallet.MarkCashBondPaid(amount);
        _walletRepo.Wallets.Add(wallet);
        return wallet;
    }

    private void TransferSucceeds() =>
        _accounts.TransferToChildAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoInternalTransfer("tr_2", "succeeded", ci.ArgAt<decimal>(3), 0m, null));

    [Fact]
    public async Task A_paid_cashbond_is_released_back_to_the_child_wallet()
    {
        var wallet = PaidWallet(1000m);
        TransferSucceeds();

        var result = await Handler().Handle(new RefundCashBondCommand(DriverId, "admin@bee.com"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, wallet.CashBondBalance);
        Assert.False(wallet.HasPaidCashBond);

        var transaction = Assert.Single(_walletRepo.Transactions);
        Assert.Equal(WalletTransactionType.CashBondRefund, transaction.Type);
        Assert.Equal(WalletBucket.CashBond, transaction.Bucket);
        Assert.Equal(WalletTransactionStatus.Completed, transaction.Status);
        Assert.Equal(1000m, transaction.Amount);
    }

    [Fact]
    public async Task An_unpaid_cashbond_has_nothing_to_refund()
    {
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        wallet.SetPayMongoWallet("wallet_a", "817797809438", "ledger_1");
        _walletRepo.Wallets.Add(wallet);

        var result = await Handler().Handle(new RefundCashBondCommand(DriverId, "admin@bee.com"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(_walletRepo.Transactions);
    }

    [Fact]
    public async Task A_rejected_transfer_leaves_the_cashbond_held_and_fails_the_command()
    {
        var wallet = PaidWallet(1000m);
        _accounts.TransferToChildAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_2", "failed", 0m, 0m, "provider error"));

        var result = await Handler().Handle(new RefundCashBondCommand(DriverId, "admin@bee.com"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1000m, wallet.CashBondBalance);
        Assert.True(wallet.HasPaidCashBond);
        Assert.Equal(WalletTransactionStatus.Failed, Assert.Single(_walletRepo.Transactions).Status);
    }
}
