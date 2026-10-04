using BeeLogistics.Modules.Payment.Application;
using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Payment.Infrastructure.Services;
using BeeLogistics.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Payments;

/// <summary>
/// Expiry of abandoned provider checkouts (GitLab #36).
///
/// <c>ExpireCheckoutAsync</c> was implemented on both gateways and called from nowhere, so an
/// abandoned PayMongo checkout stayed payable until PayMongo expired it on its own schedule - and a
/// customer paying against a stale link produced a payment that settled with nothing to attach to.
///
/// This is the first thing the reconciliation job does that changes state, at the provider as well
/// as locally, so the tests weigh heavily toward what must *not* be expired.
/// </summary>
public class AbandonedCheckoutExpiryTests
{
    private readonly FakePaymentRepository _repo = new();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IPaymentGatewayFactory _factory = Substitute.For<IPaymentGatewayFactory>();

    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    public AbandonedCheckoutExpiryTests()
    {
        _gateway.ProviderName.Returns(PaymentProviders.PayMongo);
        _factory.Get(Arg.Any<string>()).Returns(_gateway);
        _factory.GetActive().Returns(_gateway);
    }

    private AbandonedCheckoutExpirer Expirer(
        bool enabled = true, int graceHours = 12, int maxPerRun = 200) => new(
        _repo,
        _factory,
        Options.Create(new PaymentReconciliationOptions
        {
            ExpireAbandonedCheckouts = enabled,
            AbandonedCheckoutGraceHours = graceHours,
            MaxCheckoutsExpiredPerRun = maxPerRun
        }),
        NullLogger<AbandonedCheckoutExpirer>.Instance);

    /// <summary>
    /// Backdates CreatedAt, since the query selects on <c>UpdatedAt ?? CreatedAt</c> and a freshly
    /// constructed entity is always "now".
    /// </summary>
    private PaymentEntity PendingCheckout(int ageHours, PaymentMethod method = PaymentMethod.EWallet, string? providerPaymentId = "cs_abc")
    {
        var payment = PaymentEntity.Create(Guid.NewGuid(), Guid.NewGuid(), 500m, method);

        if (providerPaymentId is not null)
            payment.SetProviderCheckout(PaymentProviders.PayMongo, providerPaymentId, "https://checkout.url", payment.PaymentNumber);

        SetCreatedAt(payment, Now.AddHours(-ageHours));
        SetUpdatedAt(payment, null);
        _repo.Payments.Add(payment);
        return payment;
    }

    private static void SetCreatedAt(PaymentEntity payment, DateTime value) =>
        typeof(PaymentEntity).GetProperty(nameof(PaymentEntity.CreatedAt))!.SetValue(payment, value);

    private static void SetUpdatedAt(PaymentEntity payment, DateTime? value) =>
        typeof(PaymentEntity).GetProperty(nameof(PaymentEntity.UpdatedAt))!.SetValue(payment, value);

    // ---- Expires what it should ---------------------------------------------------------------

    [Fact]
    public async Task Expires_a_checkout_older_than_the_grace_window()
    {
        var payment = PendingCheckout(ageHours: 13);

        var expired = await Expirer(graceHours: 12).ExpireAsync(Now);

        Assert.Equal(1, expired);
        await _gateway.Received(1).ExpireCheckoutAsync("cs_abc", Arg.Any<CancellationToken>());
        Assert.Equal(PaymentStatus.Expired, payment.Status);
    }

    /// <summary>
    /// The provider must be told before the local row is marked, or a payable link survives behind
    /// a record claiming it is dead.
    /// </summary>
    [Fact]
    public async Task Leaves_the_payment_pending_when_the_provider_call_fails()
    {
        var payment = PendingCheckout(ageHours: 13);
        _gateway.ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("provider unreachable")));

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(0, expired);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task One_failure_does_not_abandon_the_rest_of_the_batch()
    {
        var doomed = PendingCheckout(ageHours: 20, providerPaymentId: "cs_bad");
        var healthy = PendingCheckout(ageHours: 19, providerPaymentId: "cs_good");

        _gateway.ExpireCheckoutAsync("cs_bad", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("provider unreachable")));

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(1, expired);
        Assert.Equal(PaymentStatus.Pending, doomed.Status);   // retried next run
        Assert.Equal(PaymentStatus.Expired, healthy.Status);
    }

    // ---- Leaves alone what it must ------------------------------------------------------------

    /// <summary>
    /// The failure that would matter most: cancelling a checkout the customer is midway through.
    /// </summary>
    [Fact]
    public async Task Never_expires_a_recent_checkout()
    {
        var payment = PendingCheckout(ageHours: 1);

        var expired = await Expirer(graceHours: 12).ExpireAsync(Now);

        Assert.Equal(0, expired);
        await _gateway.DidNotReceive().ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    /// <summary>
    /// Any webhook or state change stamps UpdatedAt, so a payment being actively worked through
    /// keeps refreshing itself out of the abandoned set even when it was created long ago.
    /// </summary>
    [Fact]
    public async Task Recent_activity_protects_an_old_payment()
    {
        var payment = PendingCheckout(ageHours: 48);
        SetUpdatedAt(payment, Now.AddMinutes(-5));

        var expired = await Expirer(graceHours: 12).ExpireAsync(Now);

        Assert.Equal(0, expired);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task Ignores_cash_payments()
    {
        var payment = PendingCheckout(ageHours: 48, method: PaymentMethod.Cash);

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(0, expired);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    /// <summary>
    /// No provider id means the gateway call never completed - there is nothing at the provider to
    /// cancel, and passing null would be a bug rather than a no-op.
    /// </summary>
    [Fact]
    public async Task Ignores_payments_that_never_reached_the_provider()
    {
        PendingCheckout(ageHours: 48, providerPaymentId: null);

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(0, expired);
        await _gateway.DidNotReceive().ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PaymentStatus.Paid)]
    [InlineData(PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Failed)]
    public async Task Ignores_payments_that_are_no_longer_pending(PaymentStatus status)
    {
        var payment = PendingCheckout(ageHours: 48);
        MoveTo(payment, status);
        SetUpdatedAt(payment, null);

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(0, expired);
        await _gateway.DidNotReceive().ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static void MoveTo(PaymentEntity payment, PaymentStatus status)
    {
        switch (status)
        {
            case PaymentStatus.Paid: payment.MarkAsPaid(Now.AddHours(-40)); break;
            case PaymentStatus.Refunded: payment.MarkAsPaid(Now.AddHours(-40)); payment.MarkAsRefunded(); break;
            case PaymentStatus.Expired: payment.MarkAsExpired(); break;
            case PaymentStatus.Failed: payment.MarkAsFailed("test"); break;
        }
    }

    // ---- Races and safety ---------------------------------------------------------------------

    /// <summary>
    /// The customer paying at the last moment. Payment has an xmin concurrency token (#30), so a
    /// webhook writing between our read and our write surfaces as a conflict - their settlement
    /// must win rather than being overwritten with Expired.
    /// </summary>
    [Fact]
    public async Task A_concurrent_settlement_wins_over_expiry()
    {
        PendingCheckout(ageHours: 13);
        _repo.FailNextSaveWithConcurrencyConflict = true;

        var expired = await Expirer().ExpireAsync(Now);

        Assert.Equal(0, expired);
    }

    [Fact]
    public async Task Kill_switch_stops_all_provider_calls()
    {
        var payment = PendingCheckout(ageHours: 48);

        var expired = await Expirer(enabled: false).ExpireAsync(Now);

        Assert.Equal(0, expired);
        await _gateway.DidNotReceive().ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    /// <summary>
    /// A backlog must not turn one hourly run into thousands of outbound provider calls.
    /// </summary>
    [Fact]
    public async Task Caps_the_number_of_expiries_per_run()
    {
        for (var i = 0; i < 10; i++)
            PendingCheckout(ageHours: 20 + i, providerPaymentId: $"cs_{i}");

        var expired = await Expirer(maxPerRun: 3).ExpireAsync(Now);

        Assert.Equal(3, expired);
        await _gateway.Received(3).ExpireCheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Refunds route by the gateway that collected the payment rather than the active one; expiry
    /// follows the same switch-safety invariant.
    /// </summary>
    [Fact]
    public async Task Routes_to_the_gateway_that_created_the_checkout()
    {
        PendingCheckout(ageHours: 13);

        await Expirer().ExpireAsync(Now);

        _factory.Received().Get(PaymentProviders.PayMongo);
        _factory.DidNotReceive().GetActive();
    }

    // ---- Options validation -------------------------------------------------------------------

    /// <summary>
    /// A one-hour window would cancel checkouts customers are still working through. Caught at
    /// startup rather than discovered in production.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Validator_rejects_a_dangerously_short_grace_window(int graceHours)
    {
        var result = new PaymentReconciliationOptionsValidator().Validate(null, new PaymentReconciliationOptions
        {
            ExpireAbandonedCheckouts = true,
            AbandonedCheckoutGraceHours = graceHours
        });

        Assert.True(result.Failed);
    }

    /// <summary>Turning the feature off is the documented way to opt out, so the window stops mattering.</summary>
    [Fact]
    public void Validator_allows_any_window_when_expiry_is_disabled()
    {
        var result = new PaymentReconciliationOptionsValidator().Validate(null, new PaymentReconciliationOptions
        {
            ExpireAbandonedCheckouts = false,
            AbandonedCheckoutGraceHours = 0
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_accepts_the_defaults()
    {
        var result = new PaymentReconciliationOptionsValidator().Validate(null, new PaymentReconciliationOptions());

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// An environment that configures nothing at all must still land on the safe values, so
    /// forgetting to set these cannot silently produce an aggressive expiry window. This is the
    /// whole config chain's fallback, pinned rather than assumed.
    /// </summary>
    [Fact]
    public void Absent_configuration_binds_to_the_safe_defaults()
    {
        var services = new ServiceCollection();
        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(new ConfigurationBuilder().Build().GetSection(PaymentReconciliationOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PaymentReconciliationOptions>>().Value;

        Assert.True(options.ExpireAbandonedCheckouts);
        Assert.Equal(12, options.AbandonedCheckoutGraceHours);
        Assert.Equal(200, options.MaxCheckoutsExpiredPerRun);
        Assert.True(new PaymentReconciliationOptionsValidator().Validate(null, options).Succeeded);
    }

    /// <summary>
    /// Partial configuration must not zero out the keys it omits - binding leaves untouched
    /// properties at their defaults rather than resetting them.
    /// </summary>
    [Fact]
    public void Partial_configuration_leaves_the_other_defaults_intact()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payment:Reconciliation:AbandonedCheckoutGraceHours"] = "24"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<PaymentReconciliationOptions>()
            .Bind(configuration.GetSection(PaymentReconciliationOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PaymentReconciliationOptions>>().Value;

        Assert.Equal(24, options.AbandonedCheckoutGraceHours);
        Assert.True(options.ExpireAbandonedCheckouts);
        Assert.Equal(200, options.MaxCheckoutsExpiredPerRun);
    }
}
