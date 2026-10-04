using BeeLogistics.Modules.Payment.Application.Gateways;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public class DriverWallet : Entity
{
    public Guid DriverId { get; private set; }
    public decimal Balance { get; private set; } // Personal wallet balance (withdrawable)
    public decimal TopUpBalance { get; private set; } // Operational/top-up wallet for cash-based delivery settlement
    public decimal PendingPayout { get; private set; } // Pending withdrawal requests

    /// <summary>
    /// The driver's cashbond, held at PayMongo's platform account, not here (issue #103).
    /// <c>0</c> = unpaid; <c>&gt;0</c> = the fixed amount currently held; back to <c>0</c> after
    /// refund. Unlike <see cref="Balance"/> and <see cref="TopUpBalance"/> this is never a running
    /// total — it is paid once, in full, and released once, in full.
    /// </summary>
    public decimal CashBondBalance { get; private set; }
    public decimal? CashJobBlockThresholdOverride { get; private set; } // Optional per-driver override for cash-job blocking threshold
    public decimal? TopUpNegativeLimitOverride { get; private set; } // Optional per-driver override for minimum allowed top-up balance
    public string? BankAccountNumber { get; private set; }
    public string? BankName { get; private set; }
    public string? AccountHolderName { get; private set; }
    public DateTime LastUpdatedAt { get; private set; }
    public uint Version { get; private set; } // Postgres xmin mapping

    // ── PayMongo child account (issue #91) ─────────────────────────────
    // A driver linked to a PayMongo child account holds their withdrawable balance THERE, not
    // here. PayMongoAccountId being non-null is the per-driver feature flag: null means this
    // wallet is on the original, fully local path and behaves exactly as it always has.
    // TopUpBalance stays local either way - it is a credit line that goes negative, which a real
    // wallet cannot do.

    /// <summary>PayMongo child account id (org_...). Non-null = this driver is on the child-wallet path.</summary>
    public string? PayMongoAccountId { get; private set; }

    /// <summary>The child's own wallet (wallet_...). Assert responses match this before trusting them.</summary>
    public string? PayMongoWalletId { get; private set; }

    /// <summary>
    /// The child wallet's account number, used as destination_account.number when pushing earnings.
    /// Cached deliberately: a freshly activated child has no transfer history to recover it from.
    /// </summary>
    public string? PayMongoAccountNumber { get; private set; }

    /// <summary>PayMongo's own ledger account for this wallet, for per-driver reconciliation.</summary>
    public string? PayMongoLedgerAccountId { get; private set; }

    /// <summary>
    /// The address the PayMongo child account was opened under.
    /// <para>
    /// Stored because it is frozen at activation and is not always the driver's plain email: if
    /// theirs was already registered with PayMongo it becomes a plus-tagged variant. Support needs
    /// to know which address the account actually carries.
    /// </para>
    /// </summary>
    public string? PayMongoAccountEmail { get; private set; }

    /// <summary>
    /// Why the last identity check failed, in terms the driver can act on — e.g. "Image quality
    /// check failed: blur detection".
    /// <para>
    /// Persisted rather than shown once: a driver who closes the app mid-onboarding would otherwise
    /// come back to a screen telling them nothing, and PayMongo's failures are usually a retake
    /// away from passing.
    /// </para>
    /// </summary>
    public string? PayMongoVerificationFailureReason { get; private set; }

    /// <summary>Where the driver sits in create -> verify -> activate. Declined is terminal.</summary>
    public PayMongoActivationStatus PayMongoActivationStatus { get; private set; }
        = PayMongoActivationStatus.None;
    
    // Navigation
    private readonly List<WalletTransaction> _transactions = new();
    public IReadOnlyCollection<WalletTransaction> Transactions => _transactions.AsReadOnly();

    private DriverWallet() { } // For EF Core

    public DriverWallet(Guid driverId)
    {
        DriverId = driverId;
        Balance = 0;
        TopUpBalance = 0;
        PendingPayout = 0;
        LastUpdatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }

    public void AddEarning(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        Balance += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddTopUp(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        TopUpBalance += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Money arrived in the driver's PayMongo child wallet from a BeeWallet QR scan, so the local
    /// mirror of that wallet has to move too.
    /// </summary>
    /// <remarks>
    /// Credits <see cref="Balance"/>, not <see cref="TopUpBalance"/>. A BeeWallet QR is issued against
    /// the driver's own PayMongo wallet, which is the withdrawable side; the Cash Wallet float is a
    /// separate concern funded through the ordinary checkout path. Distinct from
    /// <see cref="AddEarning"/> only so the two credit sources stay legible in the audit trail.
    /// </remarks>
    public void AddBeeWalletTopUp(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        Balance += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void DebitTopUpForCashSettlement(decimal amount, decimal allowedNegativeLimit)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        var resultingBalance = TopUpBalance - amount;
        if (resultingBalance < allowedNegativeLimit)
            throw new InvalidOperationException("Top-up wallet below allowed negative limit");

        TopUpBalance = resultingBalance;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Manual admin correction for a cash-collection dispute (e.g. the driver disputes a
    /// CashSettlementDebit, or genuinely under/over-remitted). Unlike
    /// <see cref="DebitTopUpForCashSettlement"/>, this is an authorized human action with its own
    /// audit trail (WalletTransactionType.CashDeficitAdjustment), so it does not enforce the
    /// negative-limit floor - an admin may need to write off debt past it.
    /// </summary>
    /// <param name="signedAmount">Positive credits the driver, negative debits them.</param>
    public void AdjustTopUpForCashDeficit(decimal signedAmount)
    {
        if (signedAmount == 0)
            throw new ArgumentException("Adjustment amount must not be zero", nameof(signedAmount));

        TopUpBalance += signedAmount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void TransferPersonalToTopUp(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (Balance < amount)
            throw new InvalidOperationException("Insufficient personal wallet balance");

        Balance -= amount;
        TopUpBalance += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void TransferTopUpToPersonal(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (TopUpBalance < amount)
            throw new InvalidOperationException("Insufficient top-up wallet balance");

        TopUpBalance -= amount;
        Balance += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Debits the mirror for a platform commission swept out of the driver's PayMongo wallet
    /// (issue #102).
    /// </summary>
    /// <remarks>
    /// Refuses to go below zero, unlike <see cref="SubtractForRefund"/>. That one models a debt we
    /// can carry; this one mirrors a wallet at PayMongo which cannot hold a negative balance, so a
    /// mirror that went negative would immediately read to
    /// <c>PayMongoBalanceReconciliationService</c> as money PayMongo is holding for us and be
    /// "credited" back as a phantom top-up. Commission that cannot be taken stays a Pending
    /// PlatformCommission row instead; that row is the debt.
    /// </remarks>
    public void DebitForPlatformCommission(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (Balance < amount)
            throw new InvalidOperationException("Insufficient balance for the platform commission");

        Balance -= amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether this driver may take cash jobs.
    /// </summary>
    /// <param name="hasUnpaidCommission">
    /// Whether any PlatformCommission row is still Pending. Passed in rather than read here: the
    /// debt lives in the ledger, which the domain object cannot see.
    /// </param>
    /// <remarks>
    /// Every booking is cash and the commission is swept from the driver's own PayMongo wallet, so
    /// eligibility is now about that wallet: it has to exist, and nothing may be owed on it.
    /// <para>
    /// This used to test <see cref="TopUpBalance"/> against a threshold — the Cash Wallet float,
    /// which is not an active feature and which nobody funds, so the test passed or failed for
    /// reasons unconnected to whether we could actually collect.
    /// </para>
    /// </remarks>
    public bool IsEligibleForCashJobs(bool hasUnpaidCommission)
        => UsesPayMongoWallet && !hasUnpaidCommission;

    /// <summary>Whether this driver has paid their cashbond and may be offered bookings.</summary>
    public bool HasPaidCashBond => CashBondBalance > 0;

    /// <summary>
    /// Records the cashbond payment once the sweep into PayMongo's platform account has landed.
    /// </summary>
    public void MarkCashBondPaid(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (CashBondBalance > 0)
            throw new InvalidOperationException("Cashbond has already been paid");

        CashBondBalance = amount;
        Touch();
    }

    /// <summary>
    /// Clears the cashbond once it has been transferred back to the driver's child wallet on
    /// offboarding. Returns the amount that was released, for the transfer call.
    /// </summary>
    public decimal ClearCashBondOnRefund()
    {
        if (CashBondBalance <= 0)
            throw new InvalidOperationException("There is no cashbond to refund");

        var amount = CashBondBalance;
        CashBondBalance = 0;
        Touch();
        return amount;
    }

    public decimal ResolveTopUpNegativeLimit(decimal defaultNegativeLimit)
    {
        return TopUpNegativeLimitOverride ?? defaultNegativeLimit;
    }

    public void ConfigureCashWalletLimits(decimal? cashJobBlockThresholdOverride, decimal? topUpNegativeLimitOverride)
    {
        CashJobBlockThresholdOverride = cashJobBlockThresholdOverride;
        TopUpNegativeLimitOverride = topUpNegativeLimitOverride;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RequestWithdrawal(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (Balance < amount)
            throw new InvalidOperationException("Insufficient balance");

        Balance -= amount;
        PendingPayout += amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void CompleteWithdrawal(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (PendingPayout < amount)
            throw new InvalidOperationException("Insufficient pending payout");

        PendingPayout -= amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RejectWithdrawal(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        if (PendingPayout < amount)
            throw new InvalidOperationException("Insufficient pending payout");

        PendingPayout -= amount;
        Balance += amount; // Return to balance
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Debit Personal balance for a refund to customer (reversal of earlier earning).
    /// Allows negative balance if driver had already withdrawn.
    /// </summary>
    public void SubtractForRefund(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amount));

        Balance -= amount;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Recovers a PayMongo account cost (one-off KYC, or monthly upkeep) from the driver's float.
    /// <para>
    /// Charged against TopUpBalance rather than the driver's PayMongo wallet for two reasons: at
    /// KYC time no wallet exists yet, and afterwards the wallet holds the driver's earnings, which
    /// are theirs. The float is the bucket the driver funded for operating costs, and it is the
    /// only one that can go negative when it runs dry.
    /// </para>
    /// </summary>
    /// <param name="allowedNegativeLimit">
    /// Floor the debit may not breach. Callers that must not create debt (the opening KYC fee)
    /// pass 0 so the charge fails rather than lending the driver money.
    /// </param>
    public void ChargeAccountFee(decimal amount, decimal allowedNegativeLimit)
    {
        if (amount <= 0)
            throw new ArgumentException("Fee amount must be positive", nameof(amount));

        var resultingBalance = TopUpBalance - amount;
        if (resultingBalance < allowedNegativeLimit)
            throw new InvalidOperationException("Cash Wallet below allowed negative limit");

        TopUpBalance = resultingBalance;
        Touch();
    }

    /// <summary>
    /// Records the child account opened for this driver, before verification has run.
    /// </summary>
    public void LinkPayMongoAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("PayMongo account id is required", nameof(accountId));

        // Re-linking would strand the balance sitting in the previously linked wallet, which this
        // aggregate can no longer see or reach.
        if (!string.IsNullOrWhiteSpace(PayMongoAccountId) && PayMongoAccountId != accountId)
            throw new InvalidOperationException(
                $"Wallet is already linked to PayMongo account {PayMongoAccountId}");

        PayMongoAccountId = accountId;
        if (PayMongoActivationStatus == PayMongoActivationStatus.None)
            PayMongoActivationStatus = PayMongoActivationStatus.Pending;
        Touch();
    }

    /// <summary>
    /// Records why identity verification failed, so the driver can be told what to fix.
    /// Passing null clears it — a retry that succeeds must not leave a stale reason on screen.
    /// </summary>
    public void SetPayMongoVerificationFailure(string? reason)
    {
        PayMongoVerificationFailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Touch();
    }

    /// <summary>Advances the onboarding state reported by PayMongo.</summary>
    public void SetPayMongoActivationStatus(PayMongoActivationStatus status)
    {
        // Declined is terminal for an account id: PayMongo's risk review cannot be appealed and the
        // account cannot be reused. Letting a later webhook move it forward would leave us believing
        // a driver can be paid when PayMongo will refuse every transfer.
        if (PayMongoActivationStatus == PayMongoActivationStatus.Declined &&
            status != PayMongoActivationStatus.Declined)
            throw new InvalidOperationException(
                "PayMongo declined this account; it cannot be moved out of Declined.");

        PayMongoActivationStatus = status;
        Touch();
    }

    /// <summary>
    /// Records the wallet PayMongo provisioned at activation. Called once the account is live.
    /// </summary>
    /// <summary>Records the address the child account was actually opened under.</summary>
    public void SetPayMongoAccountEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required", nameof(email));

        PayMongoAccountEmail = email;
        Touch();
    }

    public void SetPayMongoWallet(string walletId, string? accountNumber, string? ledgerAccountId)
    {
        if (string.IsNullOrWhiteSpace(walletId))
            throw new ArgumentException("PayMongo wallet id is required", nameof(walletId));
        if (string.IsNullOrWhiteSpace(PayMongoAccountId))
            throw new InvalidOperationException("Cannot attach a PayMongo wallet before linking an account.");

        PayMongoWalletId = walletId;
        PayMongoAccountNumber = accountNumber;
        PayMongoLedgerAccountId = ledgerAccountId;
        PayMongoActivationStatus = PayMongoActivationStatus.Activated;
        Touch();
    }

    /// <summary>
    /// Returns this wallet to the original local-balance path (phase 2 rollback).
    /// <para>
    /// Only safe once the child wallet has actually been emptied — see
    /// <c>PayMongoWalletSweepService</c>. Clearing the ids while money still sits at PayMongo would
    /// leave it unreachable: nothing left in our records would name the account holding it.
    /// </para>
    /// </summary>
    public void UnlinkPayMongoAccount()
    {
        PayMongoAccountId = null;
        PayMongoWalletId = null;
        PayMongoAccountNumber = null;
        PayMongoLedgerAccountId = null;
        PayMongoActivationStatus = PayMongoActivationStatus.None;
        Touch();
    }

    /// <summary>True when earnings and payouts should route through the driver's own PayMongo wallet.</summary>
    public bool UsesPayMongoWallet =>
        PayMongoActivationStatus == PayMongoActivationStatus.Activated &&
        !string.IsNullOrWhiteSpace(PayMongoAccountId) &&
        !string.IsNullOrWhiteSpace(PayMongoAccountNumber);

    private void Touch()
    {
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateBankDetails(string bankAccountNumber, string bankName, string accountHolderName)
    {
        BankAccountNumber = bankAccountNumber;
        BankName = bankName;
        AccountHolderName = accountHolderName;
        LastUpdatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}

