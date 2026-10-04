using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Payment.Domain;
using BeeLogistics.Modules.Revenue.Domain;
using PaymentEntity = BeeLogistics.Modules.Payment.Domain.Payment;

namespace BeeLogistics.Modules.Bookings.Application.Services;

/// <summary>
/// Builds the earnings breakdown a driver sees before accepting an offer (#58).
/// </summary>
/// <remarks>
/// The split comes from <see cref="EarningsSplit"/> and is never re-derived here. That is the
/// whole point: #28 was two independent formulas for the driver's share — the commission record
/// used <c>gross - round(gross * rate)</c>, the wallet consumers used
/// <c>round(gross * (1 - rate))</c> — which disagree by a centavo at ordinary fares (₱100.10
/// splits to 95.09 or 95.10 depending which you ask). A third formula here would quote the
/// driver a number they are then not paid.
/// </remarks>
public static class OfferEarningsCalculator
{
    public const string CashMethod = "Cash";
    public const string OnlineMethod = "Online";
    public const string TopUpWallet = "TopUpWallet";
    private const string CommissionLabel = "Platform commission";
    private const string DefaultCurrency = "PHP";

    /// <summary>
    /// The breakdown for one offer, or null when there is no fare to split — quoting ₱0.00
    /// would read as "this job pays nothing" rather than "not priced yet".
    /// </summary>
    /// <param name="paymentMethod">
    /// The booking's payment method, or null when no payment record exists yet.
    /// </param>
    /// <param name="cashChargeRate">
    /// What a cash delivery is actually settled at, when that differs from
    /// <paramref name="commissionRate"/>. CashDeliverySettlementConsumer debits this rate, so
    /// quoting the commission rate instead would show a cash driver an obligation they will not
    /// be charged. Null means the two are the same.
    /// </param>
    public static OfferEarningDetailsDto? For(
        decimal? fare,
        PaymentMethod? paymentMethod,
        decimal commissionRate,
        bool isEstimate = true,
        decimal? cashChargeRate = null,
        string currency = DefaultCurrency)
    {
        if (fare is not { } grossAmount || grossAmount <= 0m)
            return null;

        var split = EarningsSplit.For(grossAmount, commissionRate);
        var isCash = paymentMethod == PaymentMethod.Cash;

        // Routed back through EarningsSplit rather than rounded here, so the cash obligation
        // uses the one rounding rule the ledger agrees on.
        var cashOwed = isCash
            ? EarningsSplit.For(grossAmount, cashChargeRate ?? commissionRate).CommissionAmount
            : 0m;

        var deductions = new List<OfferEarningsDeductionDto>
        {
            new(CommissionLabel, split.CommissionRate, split.CommissionRate * 100m, split.CommissionAmount)
        };

        return new OfferEarningDetailsDto(
            PaymentMethod: isCash ? CashMethod : OnlineMethod,
            // Absence of a record is not evidence of an online booking; cash creates its payment
            // before the broadcast, online does not necessarily. Say so rather than imply it.
            PaymentMethodConfirmed: paymentMethod.HasValue,
            Currency: currency,
            IsEstimate: isEstimate,
            BaseEarnings: split.GrossAmount,
            Deductions: deductions,
            NetEarnings: split.DriverAmount,
            TotalNetEarnings: split.DriverAmount,
            CashSettlement: isCash
                ? new OfferCashSettlementDto(
                    CollectedFromCustomer: split.GrossAmount,
                    OwedToPlatform: cashOwed,
                    SettledFrom: TopUpWallet)
                : null);
    }

    /// <summary>
    /// Picks the payment that decides the method for a booking. A cash record wins outright:
    /// a booking can accumulate abandoned online checkouts alongside its cash payment, and the
    /// driver still collects cash in that case.
    /// </summary>
    public static PaymentMethod? MethodFrom(IEnumerable<PaymentEntity> paymentsForBooking)
    {
        PaymentMethod? found = null;
        foreach (var payment in paymentsForBooking)
        {
            if (payment.Method == PaymentMethod.Cash)
                return PaymentMethod.Cash;
            found ??= payment.Method;
        }
        return found;
    }
}
