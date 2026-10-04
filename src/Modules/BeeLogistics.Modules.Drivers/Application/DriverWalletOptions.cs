using BeeLogistics.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Drivers.Application;

public class DriverWalletOptions
{
    public const string SectionName = "DriverWallet";

    // ── Commission split (single source of truth) ──────────────────────
    // Platform takes this percentage from every completed delivery fare.
    // Default lives in Shared so Bookings, which cannot reference this module, quotes drivers
    // the same rate this class settles them at. See PlatformCommissionDefaults.
    public decimal PlatformCommissionRate { get; set; } = PlatformCommissionDefaults.Rate; // 5%

    // Derived: driver keeps the rest — always stays in sync with the commission rate.
    public decimal DriverEarningRate => 1m - PlatformCommissionRate; // 95%

    // ── Top-up / cash-job thresholds ───────────────────────────────────
    // Top-up wallet can temporarily go negative up to this value (hybrid threshold model).
    public decimal DefaultTopUpNegativeLimit { get; set; } = -500m;

    // Block new cash jobs when top-up wallet drops below this threshold.
    public decimal DefaultCashJobBlockThreshold { get; set; } = -200m;

    // Settlement debit per completed cash-delivery booking.
    // Defaults to PlatformCommissionRate; override only if cash settlement needs a different rate.
    public decimal? CashDeliveryPlatformChargeRateOverride { get; set; }
    public decimal CashDeliveryPlatformChargeRate => CashDeliveryPlatformChargeRateOverride ?? PlatformCommissionRate;

    // ── PayMongo child wallets (issue #91) ─────────────────────────────
    // Three switches, one per rollout phase, so each can be enabled and reverted independently.
    // With all three false the new code is unreachable and behaviour is identical to before.
    // Per-driver rollout needs no flag: DriverWallet.PayMongoAccountId being non-null is itself
    // the switch, so an unmigrated driver always runs the original path.

    /// <summary>Phase 1: open, verify and activate child accounts. No money moves.</summary>
    public bool PayMongoOnboardingEnabled { get; set; }

    /// <summary>Phase 2: route the driver's earnings share into their own child wallet.</summary>
    public bool PayMongoEarningsPushEnabled { get; set; }

    /// <summary>Phase 3: withdrawals leave from the driver's child wallet rather than ours.</summary>
    public bool PayMongoWithdrawalsEnabled { get; set; }

    /// <summary>
    /// Fee assumed when validating a withdrawal, before PayMongo has told us the real one.
    ///
    /// <para>
    /// PayMongo charges the transfer fee to the <b>source</b> wallet, so a driver withdrawing their
    /// whole balance is short by exactly this much and the transfer fails. Requests are therefore
    /// capped at <c>balance − this</c>.
    /// </para>
    /// <para>
    /// It is an upper-bound estimate for validation only — the published ₱10, made configurable so
    /// a repricing does not need a deploy. The fee actually charged is always read back off the
    /// transfer, never assumed: observed values have varied between channels, and a non-zero fee on
    /// a failed transfer is a quote rather than a charge.
    /// </para>
    /// </summary>
    public decimal ExpectedWithdrawalFee { get; set; } = 10m;

    /// <summary>
    /// How long a child-wallet withdrawal may sit in Approved before the reconciler polls PayMongo.
    /// <para>
    /// One value covers both rails: a PESONet transfer polled early simply reports pending and is
    /// left alone, so tracking each withdrawal's rail would buy nothing.
    /// </para>
    /// </summary>
    public int ChildWithdrawalReconcileAfterMinutes { get; set; } = 20;

    /// <summary>Withdrawals polled per run. Bounds provider API spend, not the work itself.</summary>
    public int ChildWithdrawalReconcileBatchSize { get; set; } = 100;

    /// <summary>
    /// Sweep child wallets and credit any balance PayMongo holds that our mirror has missed.
    /// <para>
    /// Separate from the phase flags because it is a safety net rather than a phase: transaction
    /// events on a child never reach the parent's webhook, so without this a missed top-up is
    /// invisible and unrecoverable.
    /// </para>
    /// </summary>
    public bool PayMongoBalanceReconciliationEnabled { get; set; }

    /// <summary>Wallets read per page. Bounds memory, not provider spend.</summary>
    public int BalanceReconcilePageSize { get; set; } = 100;

    /// <summary>
    /// Wallets examined per run. This <b>is</b> the provider-spend bound — one balance read each.
    /// </summary>
    public int BalanceReconcileMaxWalletsPerRun { get; set; } = 500;

    /// <summary>
    /// How long a wallet transfer may sit Pending before the resolver asks PayMongo about it.
    /// Short, because these are in-network and settle in seconds — a minute is already unusual.
    /// </summary>
    public int WalletTransferReconcileAfterSeconds { get; set; } = 60;

    /// <summary>Pending transfers polled per run. Bounds provider spend.</summary>
    public int WalletTransferReconcileBatchSize { get; set; } = 100;

    /// <summary>Master switch for charging PayMongo's account costs on to the driver.</summary>
    public bool AccountFeesEnabled { get; set; }

    /// <summary>One-time charge recovering PayMongo's KYC cost, taken from the opening float.</summary>
    public decimal KycFeeAmount { get; set; } = 30m;

    /// <summary>Monthly wallet upkeep, drawn against TopUpBalance.</summary>
    public decimal MonthlyWalletFeeAmount { get; set; } = 15m;

    /// <summary>
    /// Domain for the fallback per-driver email alias, e.g. <c>driver+{id}@{domain}</c>.
    ///
    /// <para>
    /// Onboarding prefers the driver's own address so PayMongo's notices and password resets reach
    /// them. This alias is used only when that address is already registered with PayMongo, which
    /// returns 409 — the email must be unique across every account on the platform, and it is
    /// frozen at activation, so there is no correcting it afterwards.
    /// </para>
    /// <para>
    /// Must be a domain PayMongo accepts; an undeliverable suffix such as <c>.local</c> is rejected.
    /// A driver with no email on file is onboarded under the alias directly.
    /// </para>
    /// </summary>
    public string PayMongoAccountEmailDomain { get; set; } = "";

    // ── Top-up settings ────────────────────────────────────────────────
    // Enforced minimum amount for top-up create.
    public decimal MinTopUpAmount { get; set; } = 50m;

    /// <summary>Optional maximum per top-up. When set, top-ups above this are rejected (typo/abuse prevention).</summary>
    public decimal? MaxTopUpAmount { get; set; }

    // Xendit invoice lifetime for top-up links (in seconds).
    public int TopUpInvoiceDurationSeconds { get; set; } = 86400;

    // ── Top-up reconciliation ──────────────────────────────────────────
    /// <summary>
    /// How many pending top-ups one reconciliation run checks against the provider. Each one
    /// costs an HTTP round-trip, so this bounds the provider API spend per run rather than the
    /// work itself: the candidates are ordered oldest-first, so anything not reached this run is
    /// still ahead of newer records on the next one.
    /// </summary>
    public int TopUpReconciliationBatchSize { get; set; } = 50;

    /// <summary>
    /// How long a withdrawal may sit Approved before reconciliation polls the provider.
    /// PayMongo allows up to 20 minutes for an InstaPay transfer to reach a final status,
    /// so anything shorter would poll transfers that are still legitimately in flight.
    /// </summary>
    public int WithdrawalReconciliationMinAgeMinutes { get; set; } = 20;

    /// <summary>Per-run cap on withdrawals polled, one provider round-trip each.</summary>
    public int WithdrawalReconciliationBatchSize { get; set; } = 50;

    /// <summary>
    /// How far back the expired-top-up recovery sweep looks, measured from creation.
    /// A top-up is closed roughly 25h after it is created, so the default gives an expired
    /// record about three further days of provider re-checks before it is left alone.
    ///
    /// This exists because expiry is otherwise a one-way door: the local timer closes a record
    /// without asking the provider whether it was paid, and ordinary reconciliation only looks
    /// at Pending rows. Set to 0 to disable the sweep.
    /// </summary>
    public int ExpiredTopUpRecheckHours { get; set; } = 96;

    /// <summary>Per-run cap on the expired recovery sweep, for the same API-spend reason as above.</summary>
    public int ExpiredTopUpRecheckBatchSize { get; set; } = 25;

    // ── Withdrawal limits ────────────────────────────────────────────────
    /// <summary>Optional maximum per withdrawal. When set, withdrawals above this are rejected.</summary>
    public decimal? MaxWithdrawalAmount { get; set; }
}

/// <summary>
/// Validates the commission rates at startup, so a bad value fails the deploy instead of the
/// driver app.
/// </summary>
/// <remarks>
/// These rates are <b>fractions</b>: 0.05 is 5%. That is easy to get wrong from the outside,
/// because the natural thing to type into Vault for "five percent" is <c>5</c> — and Vault is the
/// highest-precedence configuration source, so it overrides every default.
///
/// <see cref="Revenue.Domain.EarningsSplit"/> already refuses a rate outside 0..1, but it does so
/// at the point of use: the offers endpoint and the earnings-credit consumer. Without this
/// validator, a value of <c>5</c> boots cleanly and then presents as every driver's offer list
/// returning 500 and completed bookings never crediting — a config typo wearing the costume of an
/// outage. Failing at startup names the key instead.
/// </remarks>
public class DriverWalletOptionsValidator : IValidateOptions<DriverWalletOptions>
{
    public ValidateOptionsResult Validate(string? name, DriverWalletOptions options)
    {
        if (options.PlatformCommissionRate is < 0m or > 1m)
            return ValidateOptionsResult.Fail(
                $"{DriverWalletOptions.SectionName}:{nameof(DriverWalletOptions.PlatformCommissionRate)} "
                + $"is {options.PlatformCommissionRate}, which is outside 0..1. This is a fraction, "
                + "not a percentage — use 0.05 for 5%.");

        if (options.CashDeliveryPlatformChargeRateOverride is < 0m or > 1m)
            return ValidateOptionsResult.Fail(
                $"{DriverWalletOptions.SectionName}:{nameof(DriverWalletOptions.CashDeliveryPlatformChargeRateOverride)} "
                + $"is {options.CashDeliveryPlatformChargeRateOverride}, which is outside 0..1. This is a "
                + "fraction, not a percentage — use 0.05 for 5%.");

        return ValidateOptionsResult.Success;
    }
}
