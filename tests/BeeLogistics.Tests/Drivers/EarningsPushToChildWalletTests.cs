using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Consumers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Phase 2: routing a driver's earnings into their own PayMongo wallet (issue #91).
///
/// <para>The invariant throughout: <b>the local mirror never says a driver was paid unless the
/// money actually moved.</b> A row claiming an earning the driver cannot see or withdraw is worse
/// than a visible failure, because nothing would ever notice it.</para>
/// </summary>
public class EarningsPushToChildWalletTests
{
    private readonly FakeDriverWalletRepository _wallets = new();
    private readonly FakePaymentRepository _payments = new();
    private readonly IPlatformCommissionRepository _commissions = Substitute.For<IPlatformCommissionRepository>();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();
    private readonly IPayMongoAccountsClient _accounts = Substitute.For<IPayMongoAccountsClient>();
    private readonly List<PlatformCommission> _createdCommissions = new();

    private static readonly Guid DriverId = Guid.NewGuid();
    private static readonly Guid BookingId = Guid.NewGuid();

    public EarningsPushToChildWalletTests()
    {
        _commissions.CreateAsync(Arg.Any<PlatformCommission>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<PlatformCommission>(); _createdCommissions.Add(c); return c; });
        _commissions.GetByBookingIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _createdCommissions.FirstOrDefault(c => c.BookingId == ci.Arg<Guid>()));
    }

    private EarningCreditConsumer Consumer(bool pushEnabled) => new(
        _payments, _wallets, _commissions, _publish,
        Microsoft.Extensions.Options.Options.Create(new DriverWalletOptions
        {
            PlatformCommissionRate = 0.05m,
            PayMongoEarningsPushEnabled = pushEnabled
        }),
        NullLogger<EarningCreditConsumer>.Instance,
        _accounts);

    private static ConsumeContext<BookingCompletedEvent> Ctx(BookingCompletedEvent msg)
    {
        var c = Substitute.For<ConsumeContext<BookingCompletedEvent>>();
        c.Message.Returns(msg);
        c.CancellationToken.Returns(CancellationToken.None);
        return c;
    }

    private static BookingCompletedEvent Completed() => new()
    {
        BookingId = BookingId,
        DriverId = DriverId,
        CustomerId = Guid.NewGuid(),
        CompletedAt = DateTime.UtcNow,
        FinalFare = 500m
    };

    private void PaidCashless(decimal amount = 500m)
    {
        var p = PaymentEntity.Create(BookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        p.MarkAsPaid(DateTime.UtcNow);
        _payments.Payments.Add(p);
    }

    private DriverWallet Migrated()
    {
        var w = new DriverWallet(DriverId);
        w.LinkPayMongoAccount("org_a");
        w.SetPayMongoWallet("wallet_a", "285168654745", "ledger_1");
        _wallets.Wallets.Add(w);
        return w;
    }

    private void TransferSucceeds() =>
        _accounts.TransferToChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new PayMongoInternalTransfer("tr_1", "succeeded", ci.ArgAt<decimal>(3), 0m, null));

    // ── The happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task Migrated_driver_is_paid_into_their_own_wallet()
    {
        PaidCashless();
        var wallet = Migrated();
        TransferSucceeds();

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        await _accounts.Received(1).TransferToChildAsync(
            "org_a", "285168654745", Arg.Any<string>(), 475m,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Mirror follows the money and carries the transfer id, so reconciliation can tie the row
        // back to what PayMongo actually did.
        var row = Assert.Single(_wallets.Transactions);
        Assert.Equal(475m, row.Amount);
        Assert.Equal("tr_1", row.ProviderPaymentId);
        Assert.Equal(475m, wallet.Balance);
    }

    [Fact]
    public async Task The_split_is_unchanged_by_the_new_path()
    {
        // EarningsSplit stays the single definition (issue #28). Moving the money must not move
        // the arithmetic - 500 - round(500 * 0.05) is 475 on either path.
        PaidCashless();
        Migrated();
        TransferSucceeds();

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        Assert.Equal(475m, _wallets.Transactions[0].Amount);
    }

    // ── Failures must not fabricate an earning ─────────────────────────

    [Fact]
    public async Task A_rejected_transfer_credits_nothing_and_rethrows()
    {
        // The money never left our wallet. Crediting the mirror anyway would show the driver a
        // balance they cannot withdraw, and no later process would ever catch it.
        PaidCashless();
        var wallet = Migrated();
        _accounts.TransferToChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PayMongoInternalTransfer("tr_1", "failed", 0m, 0m,
                "validation failed for transfer_validator.source_account_balance: insufficient"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Consumer(pushEnabled: true).Consume(Ctx(Completed())));

        // The balance must not move. A claim row DOES remain, marked Failed: it was written before
        // the transfer so a redelivery would collide on it rather than send again, and recording a
        // definite failure is better than deleting it and leaving an ambiguous gap.
        Assert.Equal(0m, wallet.Balance);
        var claim = Assert.Single(_wallets.Transactions);
        Assert.Equal(WalletTransactionStatus.Failed, claim.Status);
        Assert.Null(claim.ProviderPaymentId);
    }

    [Fact]
    public async Task A_thrown_gateway_error_credits_nothing()
    {
        PaidCashless();
        var wallet = Migrated();
        _accounts.TransferToChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PaymentGatewayException("PayMongo", 503, "unavailable", "upstream down"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Consumer(pushEnabled: true).Consume(Ctx(Completed())));

        // A thrown gateway error leaves the claim Pending, not Failed: we do not know whether the
        // transfer landed. Marking it failed here would license a retry to send a second time.
        Assert.Equal(0m, wallet.Balance);
        var claim = Assert.Single(_wallets.Transactions);
        Assert.Equal(WalletTransactionStatus.Pending, claim.Status);
    }

    // ── The flag and the per-driver switch ─────────────────────────────

    [Fact]
    public async Task Flag_off_uses_the_original_local_path()
    {
        PaidCashless();
        var wallet = Migrated();

        await Consumer(pushEnabled: false).Consume(Ctx(Completed()));

        await _accounts.DidNotReceiveWithAnyArgs().TransferToChildAsync(
            default!, default!, default!, default, default!, default!, default);
        Assert.Equal(475m, wallet.Balance);
        Assert.Single(_wallets.Transactions);
    }

    [Fact]
    public async Task An_unmigrated_driver_takes_the_original_path_even_with_the_flag_on()
    {
        // PayMongoAccountId being null IS the per-driver switch: rollout is one driver at a time,
        // and everyone not yet moved runs exactly the code that runs today.
        PaidCashless();
        var plain = new DriverWallet(DriverId);
        _wallets.Wallets.Add(plain);

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        await _accounts.DidNotReceiveWithAnyArgs().TransferToChildAsync(
            default!, default!, default!, default, default!, default!, default);
        Assert.Equal(475m, plain.Balance);
    }

    [Fact]
    public async Task A_linked_but_unactivated_driver_is_not_paid_into_PayMongo()
    {
        // Linked but with no account number there is no destination_account.number to pay into,
        // so UsesPayMongoWallet is false and the local path is correct.
        PaidCashless();
        var wallet = new DriverWallet(DriverId);
        wallet.LinkPayMongoAccount("org_a");
        _wallets.Wallets.Add(wallet);

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        await _accounts.DidNotReceiveWithAnyArgs().TransferToChildAsync(
            default!, default!, default!, default, default!, default!, default);
        Assert.Equal(475m, wallet.Balance);
    }

    // ── Redelivery ─────────────────────────────────────────────────────

    [Fact]
    public async Task Redelivery_does_not_pay_the_driver_twice()
    {
        PaidCashless();
        var wallet = Migrated();
        TransferSucceeds();
        var consumer = Consumer(pushEnabled: true);
        var msg = Completed();

        await consumer.Consume(Ctx(msg));
        await consumer.Consume(Ctx(msg));

        await _accounts.Received(1).TransferToChildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Single(_wallets.Transactions);
        Assert.Equal(475m, wallet.Balance);
    }

    [Fact]
    public async Task The_reference_number_is_derived_from_the_booking()
    {
        // PayMongo rejects a reused reference number, so this is a second, provider-side guard
        // against paying twice - it holds even if our own booking-id check were bypassed.
        PaidCashless();
        Migrated();
        TransferSucceeds();

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        await _accounts.Received(1).TransferToChildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Is<string>(r => r == $"earn-{BookingId:N}"),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_claim_row_is_written_before_the_transfer_is_attempted()
    {
        // The ordering IS the safety property. If the row were written afterwards, a crash or a
        // lost save between the two would leave money in the driver's wallet that our records do
        // not know about - and the retry, finding no row, would send it a second time.
        //
        // Nothing here may lean on PayMongo rejecting a reused reference_number: that is
        // unverified, and a doomed transfer fails balance validation before any duplicate check,
        // so it cannot be tested cheaply.
        PaidCashless();
        Migrated();

        var rowsWhenTransferCalled = -1;
        _accounts.TransferToChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                rowsWhenTransferCalled = _wallets.Transactions.Count;
                return new PayMongoInternalTransfer("tr_1", "succeeded", ci.ArgAt<decimal>(3), 0m, null);
            });

        await Consumer(pushEnabled: true).Consume(Ctx(Completed()));

        Assert.Equal(1, rowsWhenTransferCalled);
    }
}
