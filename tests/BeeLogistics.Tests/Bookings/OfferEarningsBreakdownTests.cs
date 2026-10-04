using BeeLogistics.Modules.Bookings.Application.Services;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Revenue.Domain;
using BeeLogistics.Shared.Abstractions;
using Xunit;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Tests.Bookings;

/// <summary>
/// The earnings breakdown quoted to a driver before they accept an offer (#58).
///
/// The tests that matter are the equivalence ones: the driver is shown a number now and paid a
/// number later, by different code, and those must be the same number. #28 is what happens when
/// they are not.
/// </summary>
public class OfferEarningsBreakdownTests
{
    private const decimal Rate = PlatformCommissionDefaults.Rate; // 0.05

    // ------------------------------------------------------ the split is not re-derived

    /// <summary>
    /// The quote must equal what the wallet is later credited with. EarningCreditConsumer takes
    /// the driver's share from the commission record, which is EarningsSplit.DriverAmount.
    /// </summary>
    [Theory]
    [InlineData(500.00)]
    [InlineData(100.10)] // the #28 fare: independent rounding gives 95.10 here, not 95.09
    [InlineData(33.33)]
    [InlineData(0.01)]
    [InlineData(1234.57)]
    [InlineData(99.99)]
    public void Quoted_net_equals_the_amount_the_wallet_is_later_credited(decimal fare)
    {
        var quoted = OfferEarningsCalculator.For(fare, PaymentMethod.EWallet, Rate)!;

        Assert.Equal(EarningsSplit.For(fare, Rate).DriverAmount, quoted.NetEarnings);
    }

    [Theory]
    [InlineData(500.00)]
    [InlineData(100.10)]
    [InlineData(33.33)]
    public void Base_minus_deductions_is_exactly_the_net(decimal fare)
    {
        var quoted = OfferEarningsCalculator.For(fare, PaymentMethod.EWallet, Rate)!;

        Assert.Equal(quoted.BaseEarnings - quoted.Deductions.Sum(d => d.Amount), quoted.NetEarnings);
        Assert.Equal(quoted.NetEarnings, quoted.TotalNetEarnings);
    }

    [Fact]
    public void The_reported_case_from_28_is_quoted_as_the_ledger_records_it()
    {
        var quoted = OfferEarningsCalculator.For(100.10m, PaymentMethod.EWallet, Rate)!;

        Assert.Equal(100.10m, quoted.BaseEarnings);
        Assert.Equal(5.01m, Assert.Single(quoted.Deductions).Amount);
        Assert.Equal(95.09m, quoted.NetEarnings);
    }

    [Fact]
    public void The_commission_deduction_is_labelled_with_its_rate()
    {
        var deduction = Assert.Single(OfferEarningsCalculator.For(500m, PaymentMethod.EWallet, Rate)!.Deductions);

        Assert.Equal(0.05m, deduction.Rate);
        Assert.Equal(5.0m, deduction.RatePercent);
    }

    // ------------------------------------------------------------------ cash vs online

    [Fact]
    public void A_cash_offer_states_what_will_be_taken_from_the_wallet()
    {
        var quoted = OfferEarningsCalculator.For(500m, PaymentMethod.Cash, Rate)!;

        Assert.Equal(OfferEarningsCalculator.CashMethod, quoted.PaymentMethod);
        Assert.NotNull(quoted.CashSettlement);
        var settlement = quoted.CashSettlement!;
        // The driver holds the whole fare and owes the commission back.
        Assert.Equal(500m, settlement.CollectedFromCustomer);
        Assert.Equal(25m, settlement.OwedToPlatform);
        Assert.Equal(OfferEarningsCalculator.TopUpWallet, settlement.SettledFrom);
    }

    /// <summary>
    /// CashDeliverySettlementConsumer debits round(fare * cashChargeRate, 2, AwayFromZero).
    /// The quote has to be that same number or the driver is told the wrong obligation.
    /// </summary>
    [Theory]
    [InlineData(500.00)]
    [InlineData(100.10)]
    [InlineData(33.33)]
    [InlineData(0.01)]
    public void Cash_obligation_equals_what_the_settlement_consumer_debits(decimal fare)
    {
        var consumerCharge = decimal.Round(fare * Rate, 2, MidpointRounding.AwayFromZero);

        var quoted = OfferEarningsCalculator.For(fare, PaymentMethod.Cash, Rate)!;

        Assert.Equal(consumerCharge, quoted.CashSettlement!.OwedToPlatform);
    }

    /// <summary>
    /// The cash rate can be overridden away from the commission rate. When it is, the quote must
    /// follow the settlement rate, not the commission rate.
    /// </summary>
    [Fact]
    public void An_overridden_cash_rate_is_what_gets_quoted()
    {
        const decimal cashRate = 0.08m;

        var quoted = OfferEarningsCalculator.For(500m, PaymentMethod.Cash, Rate, cashChargeRate: cashRate)!;

        Assert.Equal(decimal.Round(500m * cashRate, 2, MidpointRounding.AwayFromZero), quoted.CashSettlement!.OwedToPlatform);
        // Net still reflects the commission rate: that is what the earning record uses.
        Assert.Equal(EarningsSplit.For(500m, Rate).DriverAmount, quoted.NetEarnings);
    }

    [Theory]
    [InlineData(PaymentMethod.EWallet)]
    [InlineData(PaymentMethod.CreditCard)]
    [InlineData(PaymentMethod.BankTransfer)]
    [InlineData(PaymentMethod.QrCode)]
    public void Every_non_cash_method_is_online_and_carries_no_settlement(PaymentMethod method)
    {
        var quoted = OfferEarningsCalculator.For(500m, method, Rate)!;

        Assert.Equal(OfferEarningsCalculator.OnlineMethod, quoted.PaymentMethod);
        Assert.Null(quoted.CashSettlement);
        Assert.True(quoted.PaymentMethodConfirmed);
    }

    [Fact]
    public void With_no_payment_record_the_method_is_online_but_marked_unconfirmed()
    {
        // Cash always has a payment row by offer time; online may not. Saying "Online" without
        // flagging the assumption would be asserting something not known.
        var quoted = OfferEarningsCalculator.For(500m, paymentMethod: null, Rate)!;

        Assert.Equal(OfferEarningsCalculator.OnlineMethod, quoted.PaymentMethod);
        Assert.False(quoted.PaymentMethodConfirmed);
        Assert.Null(quoted.CashSettlement);
    }

    // ----------------------------------------------------------------- absent / unpriced

    // A ₱0.00 breakdown reads as "this job pays nothing" rather than "not priced yet", so an
    // offer without a usable fare carries no earningDetails at all.

    [Fact]
    public void An_offer_with_no_fare_quotes_nothing()
        => Assert.Null(OfferEarningsCalculator.For(null, PaymentMethod.Cash, Rate));

    [Fact]
    public void An_offer_priced_at_zero_quotes_nothing()
        => Assert.Null(OfferEarningsCalculator.For(0m, PaymentMethod.Cash, Rate));

    [Fact]
    public void A_negative_fare_quotes_nothing()
        => Assert.Null(OfferEarningsCalculator.For(-1m, PaymentMethod.Cash, Rate));

    [Fact]
    public void A_quote_from_the_estimated_fare_is_flagged_as_an_estimate()
    {
        Assert.True(OfferEarningsCalculator.For(500m, PaymentMethod.Cash, Rate, isEstimate: true)!.IsEstimate);
        Assert.False(OfferEarningsCalculator.For(500m, PaymentMethod.Cash, Rate, isEstimate: false)!.IsEstimate);
    }

    // ------------------------------------------------------------- picking the method

    [Fact]
    public void Cash_wins_when_a_booking_also_has_an_abandoned_online_checkout()
    {
        var bookingId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var payments = new[]
        {
            PaymentEntity.Create(bookingId, customerId, 500m, PaymentMethod.EWallet),
            PaymentEntity.CreateCashOnDelivery(bookingId, customerId, 500m)
        };

        // The driver collects cash regardless of what else was attempted against the booking.
        Assert.Equal(PaymentMethod.Cash, OfferEarningsCalculator.MethodFrom(payments));
    }

    [Fact]
    public void With_no_payments_at_all_no_method_is_reported()
    {
        Assert.Null(OfferEarningsCalculator.MethodFrom(Array.Empty<PaymentEntity>()));
    }

    // ------------------------------------------------------ per-mode fares flow through unchanged

    /// <summary>
    /// The On-Demand premium reaches the driver with no code change here, because the split works
    /// on the gross fare and the premium is part of it.
    ///
    /// Note what this also pins: the payout is <i>proportional</i>, not full pass-through — the
    /// platform takes its share of the premium too. That is a product decision (see #67), not an
    /// oversight, and this test is where it would show up if it changed.
    /// </summary>
    [Fact]
    public void A_larger_gross_from_an_OnDemand_premium_yields_a_proportionally_larger_net()
    {
        const decimal subtotal = 124m;
        const decimal premium = 31m;   // 124 x 0.25

        var regular = OfferEarningsCalculator.For(subtotal, PaymentMethod.EWallet, Rate)!;
        var onDemand = OfferEarningsCalculator.For(subtotal + premium, PaymentMethod.EWallet, Rate)!;

        Assert.True(onDemand.NetEarnings > regular.NetEarnings);
        Assert.Equal(premium * (1 - Rate), onDemand.NetEarnings - regular.NetEarnings);
    }

    /// <summary>
    /// The mirror image, and the one worth knowing before drivers complain: a Pooling discount
    /// comes straight off the gross the split works on, so a pooled job pays the driver less. The
    /// economics only work if they land a second compatible job.
    /// </summary>
    [Fact]
    public void A_Pooling_discount_reduces_the_drivers_net_proportionally()
    {
        const decimal subtotal = 124m;
        const decimal discount = 18.60m;   // 124 x 0.15

        var regular = OfferEarningsCalculator.For(subtotal, PaymentMethod.EWallet, Rate)!;
        var pooled = OfferEarningsCalculator.For(subtotal - discount, PaymentMethod.EWallet, Rate)!;

        Assert.True(pooled.NetEarnings < regular.NetEarnings);
        Assert.Equal(discount * (1 - Rate), regular.NetEarnings - pooled.NetEarnings);
    }
}
