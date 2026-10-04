using BeeLogistics.Modules.Drivers.Application;
using BeeLogistics.Modules.Drivers.Application.Consumers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Revenue.Application.Interfaces;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Idempotency of the driver-crediting path (GitLab #29, re-homed by #31).
///
/// These assertions previously ran against WalletCreditOnPaymentSettledConsumer, which turned out
/// to be dead — its trigger event was never published, because cashless money is deliberately held
/// until the delivery completes. EarningCreditConsumer, on BookingCompletedEvent, is the path that
/// actually credits drivers, so the coverage belongs here.
/// </summary>
public class EarningCreditIdempotencyTests
{
    private readonly FakeDriverWalletRepository _wallets = new();
    private readonly FakePaymentRepository _payments = new();
    private readonly IPlatformCommissionRepository _commissions = Substitute.For<IPlatformCommissionRepository>();
    private readonly IPublishEndpoint _publish = Substitute.For<IPublishEndpoint>();

    private readonly List<PlatformCommission> _createdCommissions = new();

    private static readonly Guid DriverId = Guid.NewGuid();
    private static readonly Guid BookingId = Guid.NewGuid();

    public EarningCreditIdempotencyTests()
    {
        // Mirror a real repository: created commissions become visible to later lookups, so the
        // consumer's own idempotency guard is exercised rather than bypassed.
        _commissions.CreateAsync(Arg.Any<PlatformCommission>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<PlatformCommission>();
                _createdCommissions.Add(c);
                return c;
            });
        _commissions.GetByBookingIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _createdCommissions.FirstOrDefault(c => c.BookingId == ci.Arg<Guid>()));
    }

    private EarningCreditConsumer Consumer() => new(
        _payments,
        _wallets,
        _commissions,
        _publish,
        Microsoft.Extensions.Options.Options.Create(new DriverWalletOptions { PlatformCommissionRate = 0.05m }),
        NullLogger<EarningCreditConsumer>.Instance);

    private static ConsumeContext<BookingCompletedEvent> ContextFor(BookingCompletedEvent msg)
    {
        var ctx = Substitute.For<ConsumeContext<BookingCompletedEvent>>();
        ctx.Message.Returns(msg);
        ctx.CancellationToken.Returns(CancellationToken.None);
        return ctx;
    }

    private static BookingCompletedEvent Completed() => new()
    {
        BookingId = BookingId,
        DriverId = DriverId,
        CustomerId = Guid.NewGuid(),
        CompletedAt = DateTime.UtcNow,
        FinalFare = 500m
    };

    private void PaidCashlessPayment(decimal amount = 500m)
    {
        var payment = PaymentEntity.Create(BookingId, Guid.NewGuid(), amount, PaymentMethod.EWallet);
        payment.MarkAsPaid(DateTime.UtcNow);
        _payments.Payments.Add(payment);
    }

    [Fact]
    public async Task Credits_the_driver_once()
    {
        PaidCashlessPayment();
        _wallets.Wallets.Add(new DriverWallet(DriverId));

        await Consumer().Consume(ContextFor(Completed()));

        Assert.Single(_wallets.Transactions);
        Assert.Equal(475m, _wallets.Transactions[0].Amount); // 500 - round(500 * 0.05)
        Assert.False(_wallets.Transactions[0].IsCashEarning);
    }

    /// <summary>
    /// The fake enforces the same unique index as the database, so a double credit throws here
    /// rather than passing quietly.
    /// </summary>
    [Fact]
    public async Task Redelivery_does_not_double_credit()
    {
        PaidCashlessPayment();
        _wallets.Wallets.Add(new DriverWallet(DriverId));
        var consumer = Consumer();
        var msg = Completed();

        await consumer.Consume(ContextFor(msg));
        await consumer.Consume(ContextFor(msg));
        await consumer.Consume(ContextFor(msg));

        Assert.Single(_wallets.Transactions);
        Assert.Single(_createdCommissions);
    }

    /// <summary>
    /// The regression test for the "marker committed before the effect" bug. The commission is
    /// written first; if the wallet credit then fails, the retry must still credit the driver
    /// rather than short-circuiting on the existing commission.
    /// </summary>
    [Fact]
    public async Task Failure_after_the_commission_still_credits_the_driver_on_retry()
    {
        PaidCashlessPayment();
        _wallets.Wallets.Add(new DriverWallet(DriverId));
        var consumer = Consumer();
        var msg = Completed();
        _wallets.FailNextApplyTransaction = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(ContextFor(msg)));
        Assert.Single(_createdCommissions);      // commission landed
        Assert.Empty(_wallets.Transactions);      // driver did not

        // MassTransit redelivers.
        await consumer.Consume(ContextFor(msg));

        Assert.Single(_wallets.Transactions);
        Assert.Equal(475m, _wallets.Transactions[0].Amount);
        Assert.Single(_createdCommissions);       // and no duplicate commission
    }

    /// <summary>
    /// Cash earnings are recorded for history but must not change the withdrawable balance, and
    /// must be flagged so the balance calculation can exclude them without inspecting text.
    /// </summary>
    [Fact]
    public async Task Cash_earning_is_recorded_as_history_only()
    {
        var payment = PaymentEntity.CreateCashOnDelivery(BookingId, Guid.NewGuid(), 500m);
        _payments.Payments.Add(payment);
        _wallets.Wallets.Add(new DriverWallet(DriverId));

        await Consumer().Consume(ContextFor(Completed()));

        Assert.Single(_wallets.Transactions);
        Assert.True(_wallets.Transactions[0].IsCashEarning);
        Assert.Equal(0m, await _wallets.GetCalculatedPersonalBalanceAsync(_wallets.Wallets[0].Id));
    }

    /// <summary>
    /// The cash branch used to look up one description and store a different one, so its guard
    /// never matched and every redelivery appended another row.
    /// </summary>
    [Fact]
    public async Task Redelivered_cash_earning_is_not_duplicated()
    {
        var payment = PaymentEntity.CreateCashOnDelivery(BookingId, Guid.NewGuid(), 500m);
        _payments.Payments.Add(payment);
        _wallets.Wallets.Add(new DriverWallet(DriverId));
        var consumer = Consumer();
        var msg = Completed();

        await consumer.Consume(ContextFor(msg));
        await consumer.Consume(ContextFor(msg));

        Assert.Single(_wallets.Transactions);
    }

    [Fact]
    public async Task Sale_is_published_for_accounting_with_a_balanced_split()
    {
        PaidCashlessPayment(amount: 100.10m);
        _wallets.Wallets.Add(new DriverWallet(DriverId));

        await Consumer().Consume(ContextFor(Completed()));

        await _publish.Received(1).Publish(
            Arg.Is<SaleRecordedEvent>(e =>
                e.Amount == 100.10m &&
                e.PlatformCommissionAmount + e.DriverAmount == 100.10m),
            Arg.Any<CancellationToken>());
    }
}
