using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

/// <summary>
/// Represents a wallet transaction for a driver.
/// In the independent-driver model, earnings come from completed bookings (not dispatches).
/// </summary>
public class WalletTransaction : Entity
{
    public Guid WalletId { get; private set; }
    public WalletTransactionType Type { get; private set; }
    public WalletBucket Bucket { get; private set; } // Personal or TopUp bucket impacted by this transaction
    public decimal Amount { get; private set; }
    public WalletTransactionStatus Status { get; private set; } // Completed, Pending, Failed
    public string Description { get; private set; } = string.Empty;
    
    /// <summary>
    /// Related booking ID (independent-driver model - earnings from completed bookings)
    /// </summary>
    public Guid? RelatedBookingId { get; private set; }
    public Guid? RelatedWithdrawalRequestId { get; private set; }
    public DateTime TransactionDate { get; private set; }

    /// <summary>
    /// The provider payment this row settles — a Xendit invoice id or a PayMongo checkout/payment
    /// id. Null for anything not driven by a provider payment (earnings, transfers, adjustments).
    /// </summary>
    /// <remarks>
    /// Backs the unique index that makes double-crediting a top-up structurally impossible
    /// (GitLab #66). Idempotency used to key on <see cref="Description"/> — free text with the
    /// provider id embedded in prose, and a code comment warning the wording had to stay
    /// byte-identical forever. That is not a hypothetical failure: the cash-earning path once
    /// looked up one description and stored a different one, so its guard never matched, every
    /// redelivery appended another row, and a migration had to soft-delete the duplicates.
    ///
    /// Money identity does not belong in display text that anyone may reformat.
    /// </remarks>
    public string? ProviderPaymentId { get; private set; }

    /// <summary>
    /// True for an earning the driver already collected as physical cash, which is recorded for
    /// history but does not increase the withdrawable digital balance.
    ///
    /// Replaces testing <c>Description.Contains("(cash)")</c> in the balance calculation: money
    /// classification must not depend on display text that anyone may reformat. Only meaningful
    /// for <see cref="WalletTransactionType.Earning"/>; false everywhere else.
    /// </summary>
    public bool IsCashEarning { get; private set; }

    /// <summary>
    /// Which package-insurance policy year (1, 2, 3…) this row pays for. Null for every
    /// transaction type other than <see cref="WalletTransactionType.PackageInsurancePayment"/>.
    /// </summary>
    /// <remarks>
    /// Set at claim time — the initial insert — unlike <see cref="ProviderPaymentId"/>, which is
    /// only known after the provider call. That timing is what makes the filtered unique index on
    /// <c>(WalletId, Type, PolicyYearNumber)</c> a genuine claim-time race guard: two concurrent
    /// claims for the same driver+year collide on <c>INSERT</c>, not merely on a later read.
    /// </remarks>
    public int? PolicyYearNumber { get; private set; }

    // Navigation
    public DriverWallet Wallet { get; private set; } = null!;

    private WalletTransaction() { } // For EF Core

    public WalletTransaction(
        Guid walletId,
        WalletTransactionType type,
        WalletBucket bucket,
        decimal amount,
        WalletTransactionStatus status,
        string description,
        Guid? relatedBookingId = null,
        Guid? relatedWithdrawalRequestId = null,
        bool isCashEarning = false,
        string? providerPaymentId = null,
        int? policyYearNumber = null)
    {
        ProviderPaymentId = string.IsNullOrWhiteSpace(providerPaymentId) ? null : providerPaymentId.Trim();
        WalletId = walletId;
        Type = type;
        Bucket = bucket;
        Amount = amount;
        Status = status;
        Description = description;
        RelatedBookingId = relatedBookingId;
        RelatedWithdrawalRequestId = relatedWithdrawalRequestId;
        IsCashEarning = isCashEarning;
        PolicyYearNumber = policyYearNumber;
        TransactionDate = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records the provider transfer this row settled, once it is known.
    /// <para>
    /// Set after the fact because the row is written <i>before</i> the transfer is attempted — the
    /// claim has to exist first so a redelivery collides on the unique index rather than sending
    /// money twice, and the provider id only exists afterwards.
    /// </para>
    /// </summary>
    public void SetProviderPaymentId(string providerPaymentId)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId))
            throw new ArgumentException("Provider payment id is required", nameof(providerPaymentId));

        ProviderPaymentId = providerPaymentId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        Status = WalletTransactionStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed()
    {
        Status = WalletTransactionStatus.Failed;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum WalletTransactionType
{
    Earning = 0,        // Earnings from completed deliveries
    Withdrawal = 1,     // Withdrawal request
    Payout = 2,         // Completed payout
    Refund = 3,         // Refund (if needed)
    TopUp = 4,          // Cash-in via Xendit to top-up wallet
    CashSettlementDebit = 5, // Debit per completed cash-delivery settlement
    WalletTransferIn = 6,    // Transfer credit into a wallet bucket
    WalletTransferOut = 7,   // Transfer debit out of a wallet bucket
    CashDeficitAdjustment = 8, // Manual adjustment for settlement deficit
    EarningReversal = 9,        // Debit when customer is refunded (driver had been credited on delivery complete)
    AccountFee = 10,            // PayMongo child-account costs recovered from the driver (KYC once, upkeep monthly)

    /// <summary>
    /// The platform's share of a cash delivery, swept from the driver's own PayMongo wallet to the
    /// platform's (issue #102).
    /// </summary>
    /// <remarks>
    /// On a cash job the customer pays the driver directly, so the driver ends up holding our
    /// commission along with their own earnings. This is the row that takes it back.
    ///
    /// <para>
    /// The row IS the debt: <see cref="WalletTransactionStatus.Pending"/> means owed,
    /// <see cref="WalletTransactionStatus.Completed"/> means the transfer landed. Nothing is stored
    /// on the wallet itself, so what a driver owes is always the sum of these rows and can never
    /// drift from them.
    /// </para>
    /// <para>
    /// Distinct from <see cref="CashSettlementDebit"/>, which recorded the same charge against the
    /// Cash Wallet float and moved no money. Kept separate so the two remain legible in history.
    /// </para>
    /// </remarks>
    PlatformCommission = 11,

    /// <summary>
    /// The driver's upfront cashbond, swept from their PayMongo child wallet into the platform's
    /// (issue #103). One-time, fixed by vehicle type — never accrued, never deducted from.
    /// </summary>
    CashBondPayment = 12,

    /// <summary>
    /// The cashbond released back to the driver's PayMongo child wallet on offboarding, the
    /// reverse of <see cref="CashBondPayment"/>.
    /// </summary>
    CashBondRefund = 13,

    /// <summary>
    /// A driver's annual package-insurance premium, paid into the platform wallet (issue #104).
    /// Recurring, unlike <see cref="CashBondPayment"/> — one row per covered policy year, tracked
    /// via <see cref="WalletTransaction.PolicyYearNumber"/>.
    /// </summary>
    PackageInsurancePayment = 14
}

public enum WalletBucket
{
    Personal = 0,
    TopUp = 1,
    CashBond = 2,
    PackageInsurance = 3
}

public enum WalletTransactionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}

