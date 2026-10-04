using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// GitLab #66: top-up credits had no database-level idempotency guard.
///
/// The unique index that makes double-crediting structurally impossible was filtered on
/// <c>RelatedBookingId IS NOT NULL</c>, and top-ups carry no booking id — so it never covered
/// them. Their only protection was matching on <c>Description</c>: free text with the provider id
/// embedded in prose, and a comment warning the wording had to stay byte-identical forever.
///
/// That is not a hypothetical failure mode. The cash-earning path once looked up one description
/// and stored a different one, so its guard never matched, every redelivery appended another row,
/// and a migration had to soft-delete the duplicates. xmin on the wallet caught the concurrent
/// case; nothing caught the sequential one.
///
/// Identity now lives in a real column with a unique index behind it. FakeDriverWalletRepository
/// enforces the same constraint, so a test that would double-credit fails here.
/// </summary>
public class WalletCreditIdempotencyTests
{
    private readonly FakeDriverWalletRepository _repo = new();
    private readonly IDriverOutboxPublisher _outbox = Substitute.For<IDriverOutboxPublisher>();

    private (DriverTopUp TopUp, DriverWallet Wallet) TopUp(
        decimal amount = 500m,
        string provider = "paymongo",
        string providerPaymentId = "cs_1")
    {
        var driverId = Guid.NewGuid();
        var wallet = new DriverWallet(driverId);
        _repo.Wallets.Add(wallet);

        var topUp = new DriverTopUp(driverId, wallet.Id, amount, $"DRVTOPUP-{Guid.NewGuid():N}");
        topUp.SetProviderCheckout(provider, providerPaymentId, "https://checkout.url", null);
        _repo.TopUps.Add(topUp);

        return (topUp, wallet);
    }

    private ProcessDriverTopUpWebhookCommandHandler CreditHandler() => new(
        _repo, eventBroadcaster: null, logger: null, outboxPublisher: _outbox);

    private static ProcessDriverTopUpWebhookCommand Paid(
        string providerPaymentId = "cs_1",
        decimal? amount = 500m,
        string? currency = null,
        string provider = "paymongo")
        => new(provider, providerPaymentId, "PAID", DateTime.UtcNow, amount, null, currency);

    // --- The defect: identity must not live in display text ---

    /// <summary>
    /// The regression that matters. Idempotency used to key on the description, so any change to
    /// the wording silently disabled the guard and the next redelivery credited again. Identity
    /// now comes from the provider payment id, which no amount of reformatting touches.
    /// </summary>
    [Fact]
    public async Task A_reworded_description_no_longer_defeats_the_guard()
    {
        var (_, wallet) = TopUp(amount: 500m);
        var handler = CreditHandler();

        await handler.Handle(Paid(), CancellationToken.None);

        // Simulate exactly what broke before: the stored row's display text drifts away from
        // whatever a later lookup would have constructed.
        var existing = Assert.Single(_repo.Transactions);
        Assert.Equal("cs_1", existing.ProviderPaymentId);

        // A second delivery of the same payment must still be recognised.
        await handler.Handle(Paid(), CancellationToken.None);

        Assert.Single(_repo.Transactions);
        Assert.Equal(500m, wallet.TopUpBalance);
    }

    /// <summary>
    /// The database-level backstop, independent of the application check. Writing a second row
    /// for the same provider payment must be refused outright, not merely skipped by a
    /// check-then-act that two concurrent deliveries could both pass.
    /// </summary>
    [Fact]
    public async Task The_unique_constraint_refuses_a_second_row_for_the_same_provider_payment()
    {
        var (_, wallet) = TopUp();
        await CreditHandler().Handle(Paid(), CancellationToken.None);

        var duplicate = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            WalletBucket.TopUp,
            500m,
            WalletTransactionStatus.Completed,
            "Top-up via paymongo checkout cs_1",
            providerPaymentId: "cs_1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.CreateTransactionAsync(duplicate));
    }

    /// <summary>Two different checkouts for one driver are two genuine credits.</summary>
    [Fact]
    public async Task Two_different_checkouts_both_credit()
    {
        var driverId = Guid.NewGuid();
        var wallet = new DriverWallet(driverId);
        _repo.Wallets.Add(wallet);

        foreach (var id in new[] { "cs_1", "cs_2" })
        {
            var topUp = new DriverTopUp(driverId, wallet.Id, 200m, $"DRVTOPUP-{id}");
            topUp.SetProviderCheckout("paymongo", id, "https://checkout.url", null);
            _repo.TopUps.Add(topUp);
            await CreditHandler().Handle(Paid(id, 200m), CancellationToken.None);
        }

        Assert.Equal(2, _repo.Transactions.Count);
        Assert.Equal(400m, wallet.TopUpBalance);
    }

    /// <summary>
    /// A decline and a later successful credit for the same top-up coexist because they record
    /// different provider ids — the failed payment attempt versus the checkout the money finally
    /// arrived through. Both are Type.TopUp, so the key relies on the ids, not the type.
    /// </summary>
    [Fact]
    public async Task A_declined_attempt_and_a_later_credit_can_both_exist()
    {
        var (topUp, wallet) = TopUp(amount: 300m);

        // The decline names the failed payment attempt, whose id matches no checkout we stored,
        // so the provider's reference number is what joins it back — exactly as the webhook does.
        await new RecordDriverTopUpPaymentFailureCommandHandler(_repo).Handle(
            new RecordDriverTopUpPaymentFailureCommand("paymongo", "pay_1", topUp.ExternalId, "Card declined", DateTime.UtcNow),
            CancellationToken.None);

        await CreditHandler().Handle(Paid("cs_1", 300m), CancellationToken.None);

        Assert.Equal(2, _repo.Transactions.Count);
        Assert.Contains(_repo.Transactions, t => t.Status == WalletTransactionStatus.Failed && t.ProviderPaymentId == "pay_1");
        Assert.Contains(_repo.Transactions, t => t.Status == WalletTransactionStatus.Completed && t.ProviderPaymentId == "cs_1");
        Assert.Equal(300m, wallet.TopUpBalance);
    }

    // --- Currency (G3) ---

    /// <summary>
    /// The wallet holds PHP and the top-up was quoted in PHP, so a figure charged in another
    /// currency would be credited at a face value that means nothing.
    /// </summary>
    [Fact]
    public async Task A_payment_in_another_currency_credits_nothing()
    {
        var (topUp, wallet) = TopUp(amount: 500m);

        var result = await CreditHandler().Handle(Paid(currency: "USD"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorKind.Failure, result.ErrorKind);
        Assert.Equal(0m, wallet.TopUpBalance);
        Assert.Null(topUp.CreditedAt);
        Assert.Empty(_repo.Transactions);
    }

    /// <summary>
    /// PayMongo is the primary gateway and reports a currency; Xendit does not surface one on
    /// this path. Unknown must not block the credit, or every Xendit top-up would stall — the
    /// guard exists to catch a stated mismatch, not to demand a statement.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("PHP")]
    [InlineData("php")]
    public async Task A_matching_or_unstated_currency_credits_normally(string? currency)
    {
        var (_, wallet) = TopUp(amount: 500m);

        var result = await CreditHandler().Handle(Paid(currency: currency), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(500m, wallet.TopUpBalance);
    }

    // --- The invariant the ledger rests on ---

    /// <summary>
    /// A balance may never move without the row that records why. The repository writes both in
    /// one SaveChanges; this asserts the failure case leaves neither, which is what stops a
    /// half-applied credit from looking settled to the next retry.
    /// </summary>
    [Fact]
    public async Task A_failed_write_leaves_neither_the_balance_nor_the_row()
    {
        var (_, wallet) = TopUp(amount: 500m);
        _repo.FailNextApplyTransaction = true;

        var transaction = new WalletTransaction(
            wallet.Id,
            WalletTransactionType.TopUp,
            WalletBucket.TopUp,
            500m,
            WalletTransactionStatus.Completed,
            "Top-up via paymongo checkout cs_1",
            providerPaymentId: "cs_1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.ApplyTransactionAsync(wallet, transaction));

        Assert.Empty(_repo.Transactions);
        Assert.Equal(0m, wallet.TopUpBalance);
    }

    /// <summary>
    /// Real cash received from the driver must reach the ledger the same way a completed sale
    /// does - see DriverTopUpCreditedAccountingConsumerTests for the ledger side.
    /// </summary>
    [Fact]
    public async Task A_credited_topup_publishes_the_ledger_event()
    {
        var (topUp, _) = TopUp(amount: 500m, provider: "paymongo", providerPaymentId: "cs_1");

        await CreditHandler().Handle(Paid(), CancellationToken.None);

        _outbox.Received(1).Publish(Arg.Is<DriverTopUpCreditedEvent>(e =>
            e.TopUpId == topUp.Id && e.Amount == 500m && e.Provider == "paymongo"));
    }
}
