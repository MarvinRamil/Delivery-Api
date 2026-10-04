using BeeLogistics.Modules.Drivers.Application.Handlers;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// GitLab #64: a top-up the driver actually paid for could end up marked Expired with the wallet
/// never credited, no alert, and no fix short of hand-written SQL.
///
/// Three defects compounded: reconciliation checked the *newest* pending records so the stuck
/// ones starved, the expiry timer closed records without ever asking the provider, and the two
/// refuse-to-credit paths returned a failure that was only logged at Debug.
/// </summary>
public class DriverTopUpRecoveryTests
{
    private readonly FakeDriverWalletRepository _repo = new();

    private DriverTopUp TopUp(
        Guid driverId,
        Guid walletId,
        decimal amount = 500m,
        string providerPaymentId = "cs_1",
        DateTime? createdAt = null)
    {
        var topUp = new DriverTopUp(driverId, walletId, amount, $"DRVTOPUP-{Guid.NewGuid():N}");
        topUp.SetProviderCheckout("paymongo", providerPaymentId, "https://checkout.url", null);

        if (createdAt.HasValue)
            BackdateCreatedAt(topUp, createdAt.Value);

        _repo.TopUps.Add(topUp);
        return topUp;
    }

    /// <summary>CreatedAt is set by the base entity and has no setter; ordering is what is under test.</summary>
    private static void BackdateCreatedAt(DriverTopUp topUp, DateTime createdAt)
        => typeof(BeeLogistics.Shared.Abstractions.Entity)
            .GetProperty(nameof(Entity.CreatedAt))!
            .SetValue(topUp, createdAt);

    private DriverWallet Wallet(Guid driverId)
    {
        var wallet = new DriverWallet(driverId);
        _repo.Wallets.Add(wallet);
        return wallet;
    }

    // --- Defect 1: reconciliation starvation ---

    /// <summary>
    /// The pending pool is every abandoned checkout of the last day, so it routinely exceeds the
    /// per-run cap. Taking the newest meant the same recent records were re-checked forever and
    /// the genuinely stuck ones were never reached again.
    /// </summary>
    [Fact]
    public async Task Reconciliation_takes_the_oldest_pending_top_ups_not_the_newest()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        // Oldest last, so a naive "first N of insertion order" would also pass by accident.
        for (var daysAgo = 1; daysAgo <= 5; daysAgo++)
            TopUp(driverId, wallet.Id, providerPaymentId: $"cs_{daysAgo}", createdAt: now.AddDays(-daysAgo));

        var batch = await _repo.GetTopUpsAwaitingReconciliationAsync(now, limit: 2);

        Assert.Equal(new[] { "cs_5", "cs_4" }, batch.Select(t => t.ProviderPaymentId).ToArray());
    }

    /// <summary>
    /// A record with no provider checkout id has nothing to ask the provider about. It used to be
    /// skipped inside the loop, silently consuming a slot in the per-run budget on every run.
    /// </summary>
    [Fact]
    public async Task Reconciliation_ignores_top_ups_with_no_provider_checkout()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        var orphan = new DriverTopUp(driverId, wallet.Id, 500m, "DRVTOPUP-orphan");
        BackdateCreatedAt(orphan, now.AddDays(-9));
        _repo.TopUps.Add(orphan);

        TopUp(driverId, wallet.Id, providerPaymentId: "cs_real", createdAt: now.AddDays(-1));

        var batch = await _repo.GetTopUpsAwaitingReconciliationAsync(now, limit: 10);

        Assert.Equal(new[] { "cs_real" }, batch.Select(t => t.ProviderPaymentId).ToArray());
    }

    /// <summary>Records younger than the cutoff are left alone so webhooks get their chance first.</summary>
    [Fact]
    public async Task Reconciliation_leaves_top_ups_younger_than_the_cutoff_alone()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        TopUp(driverId, wallet.Id, providerPaymentId: "cs_fresh", createdAt: now.AddMinutes(-2));

        var batch = await _repo.GetTopUpsAwaitingReconciliationAsync(now.AddMinutes(-15), limit: 10);

        Assert.Empty(batch);
    }

    // --- Defect 2: expiry was a one-way door ---

    /// <summary>
    /// The expiry job closes records on a local timer without consulting the provider, and normal
    /// reconciliation only looks at Pending. Without this sweep a paid top-up closed by the timer
    /// is written off permanently.
    /// </summary>
    [Fact]
    public async Task Expired_but_uncredited_top_ups_are_still_offered_for_recovery()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        var expired = TopUp(driverId, wallet.Id, providerPaymentId: "cs_expired", createdAt: now.AddHours(-30));
        expired.MarkAsExpired();

        var batch = await _repo.GetUncreditedExpiredTopUpsAsync(now.AddHours(-96), limit: 10);

        Assert.Equal(new[] { "cs_expired" }, batch.Select(t => t.ProviderPaymentId).ToArray());
    }

    /// <summary>An expired record that was credited needs no second look.</summary>
    [Fact]
    public async Task The_recovery_sweep_skips_expired_top_ups_that_were_credited()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        var credited = TopUp(driverId, wallet.Id, providerPaymentId: "cs_done", createdAt: now.AddHours(-30));
        credited.MarkCredited();

        var batch = await _repo.GetUncreditedExpiredTopUpsAsync(now.AddHours(-96), limit: 10);

        Assert.Empty(batch);
    }

    /// <summary>The sweep is bounded; records past the window stop being re-checked forever.</summary>
    [Fact]
    public async Task The_recovery_sweep_stops_at_the_lookback_window()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var now = DateTime.UtcNow;

        var ancient = TopUp(driverId, wallet.Id, providerPaymentId: "cs_ancient", createdAt: now.AddDays(-30));
        ancient.MarkAsExpired();

        var batch = await _repo.GetUncreditedExpiredTopUpsAsync(now.AddHours(-96), limit: 10);

        Assert.Empty(batch);
    }

    // --- Out-of-order webhooks must not contradict a credited wallet ---

    /// <summary>
    /// The realistic sequence: attempt one is declined, the driver retries on the same checkout
    /// link and succeeds, the wallet is credited. A redelivered decline - or the expiry sweep
    /// arriving late - must not then flip the record to a terminal state while the money stays.
    /// </summary>
    [Fact]
    public void A_settled_top_up_cannot_be_expired_or_failed_after_the_fact()
    {
        var topUp = TopUp(Guid.NewGuid(), Guid.NewGuid());
        topUp.MarkAsPaid(DateTime.UtcNow);
        topUp.MarkCredited();

        topUp.MarkAsExpired();
        topUp.MarkAsFailed("late decline");
        topUp.RecordFailedAttempt("late decline");

        Assert.Equal(DriverTopUpStatus.Paid, topUp.Status);
        Assert.Null(topUp.FailureReason);
    }

    /// <summary>
    /// CreditedAt alone is enough: it is set in the same step as the balance change, and a
    /// webhook can land between it and the status write.
    /// </summary>
    [Fact]
    public void A_credited_top_up_is_settled_even_before_its_status_is_written()
    {
        var topUp = TopUp(Guid.NewGuid(), Guid.NewGuid());
        topUp.MarkCredited();

        topUp.MarkAsExpired();

        Assert.True(topUp.IsSettled);
        Assert.NotEqual(DriverTopUpStatus.Expired, topUp.Status);
    }

    /// <summary>An unpaid record still expires normally - the guard must not disable the sweep.</summary>
    [Fact]
    public void An_unpaid_top_up_still_expires()
    {
        var topUp = TopUp(Guid.NewGuid(), Guid.NewGuid());

        topUp.MarkAsExpired();

        Assert.Equal(DriverTopUpStatus.Expired, topUp.Status);
    }

    // --- Defect 3: a refusal to credit must never be silent ---

    /// <summary>
    /// "No such top-up" is the ordinary case - every booking-payment webhook reaches this handler
    /// too - so it must stay distinguishable from a top-up we found and declined to credit.
    /// Callers log the two at different levels, and only the second is money we are holding.
    /// </summary>
    [Fact]
    public async Task A_checkout_that_is_not_a_top_up_reports_NotFound_rather_than_failure()
    {
        var handler = new ProcessDriverTopUpWebhookCommandHandler(_repo);

        var result = await handler.Handle(
            new ProcessDriverTopUpWebhookCommand("paymongo", "cs_unknown", "PAID", DateTime.UtcNow, 500m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorKind.NotFound, result.ErrorKind);
    }

    /// <summary>
    /// The provider took a different amount than we asked for. That is money we are holding, so
    /// it must surface as a plain failure - not NotFound, which callers treat as routine noise.
    /// </summary>
    [Fact]
    public async Task An_amount_mismatch_is_reported_as_a_failure_and_credits_nothing()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        var topUp = TopUp(driverId, wallet.Id, amount: 500m, providerPaymentId: "cs_mismatch");

        var handler = new ProcessDriverTopUpWebhookCommandHandler(
            _repo, eventBroadcaster: null, NullLogger<ProcessDriverTopUpWebhookCommandHandler>.Instance);

        var result = await handler.Handle(
            new ProcessDriverTopUpWebhookCommand("paymongo", "cs_mismatch", "PAID", DateTime.UtcNow, 250m),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ResultErrorKind.Failure, result.ErrorKind);
        Assert.Equal(0m, wallet.TopUpBalance);
        Assert.Null(topUp.CreditedAt);
    }

    /// <summary>Redelivery must not credit twice; the CreditedAt guard short-circuits.</summary>
    [Fact]
    public async Task A_redelivered_paid_webhook_credits_only_once()
    {
        var driverId = Guid.NewGuid();
        var wallet = Wallet(driverId);
        TopUp(driverId, wallet.Id, amount: 500m, providerPaymentId: "cs_dup");

        var handler = new ProcessDriverTopUpWebhookCommandHandler(_repo);
        var command = new ProcessDriverTopUpWebhookCommand("paymongo", "cs_dup", "PAID", DateTime.UtcNow, 500m);

        await handler.Handle(command, CancellationToken.None);
        await handler.Handle(command, CancellationToken.None);

        Assert.Equal(500m, wallet.TopUpBalance);
        Assert.Single(_repo.Transactions, t => t.Type == WalletTransactionType.TopUp);
    }
}
