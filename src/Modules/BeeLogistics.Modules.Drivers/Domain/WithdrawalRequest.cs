using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public class WithdrawalRequest : Entity
{
    public Guid DriverId { get; private set; }
    public Guid WalletId { get; private set; }
    public decimal Amount { get; private set; }
    public WithdrawalStatus Status { get; private set; } // Pending, Approved, Rejected, Completed
    public string BankAccountNumber { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public string AccountHolderName { get; private set; } = string.Empty;
    public string? RejectionReason { get; private set; }

    /// <summary>Gateway that owns this payout ("xendit" | "paymongo"). Webhooks/reconciliation route by this.</summary>
    public string Provider { get; private set; } = "xendit";
    /// <summary>Provider payout id: Xendit payout id / PayMongo transfer id.</summary>
    public string? ProviderDisbursementId { get; private set; }

    /// <summary>
    /// The fee the provider actually charged, as they reported it.
    /// <para>
    /// Stored rather than recomputed because PayMongo takes it from the <b>source</b> wallet, so a
    /// child-wallet withdrawal removes <c>Amount + Fee</c> — and by the time the withdrawal settles,
    /// the transfer that reported the fee is no longer in hand. Not
    /// <c>DriverWalletOptions.ExpectedWithdrawalFee</c>: that is an upper-bound estimate for
    /// validating the request, and observed fees have varied between channels.
    /// </para>
    /// <para>Zero for the platform path, where the fee is ours and never touches the driver.</para>
    /// </summary>
    public decimal Fee { get; private set; }

    /// <summary>
    /// Records what the provider charged, once the transfer has been accepted.
    /// </summary>
    public void RecordProviderFee(decimal fee)
    {
        if (fee < 0m)
            throw new ArgumentOutOfRangeException(nameof(fee), fee, "A provider fee cannot be negative.");

        Fee = fee;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// What actually left the driver's wallet: the transfer plus the fee taken from the same
    /// wallet. This, not <see cref="Amount"/>, is what the local mirror must come down by.
    /// </summary>
    public decimal TotalDebitedFromSource => Amount + Fee;

    // TODO(provider-cleanup): legacy Xendit-named column. Frozen (no writers) since the
    // provider-agnostic columns above replaced it; backfilled by AddDriverProviderColumns.
    public string? XenditDisbursementId { get; private set; }
    /// <summary>Where the money went: a bank/e-wallet account, or a scanned QR Ph code.</summary>
    public WithdrawalDestinationType DestinationType { get; private set; } = WithdrawalDestinationType.BankAccount;

    /// <summary>Provider QR id for QR Ph payouts; null for bank transfers. Polled during reconciliation.</summary>
    public string? QrId { get; private set; }

    /// <summary>Optional idempotency key; when set, duplicate requests with same driver+key return existing withdrawal.</summary>
    public string? IdempotencyKey { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public Guid? ProcessedByUserId { get; private set; }
    
    // Navigation
    public DriverWallet Wallet { get; private set; } = null!;

    private WithdrawalRequest() { } // For EF Core

    /// <summary>Create with a known Id (e.g. for reference_id sent to Xendit before persisting).</summary>
    public WithdrawalRequest(
        Guid id,
        Guid driverId,
        Guid walletId,
        decimal amount,
        string bankAccountNumber,
        string bankName,
        string accountHolderName,
        bool autoApprove = true,
        string? idempotencyKey = null,
        WithdrawalDestinationType destinationType = WithdrawalDestinationType.BankAccount)
    {
        Id = id;
        DriverId = driverId;
        WalletId = walletId;
        Amount = amount;
        BankAccountNumber = bankAccountNumber;
        BankName = bankName;
        AccountHolderName = accountHolderName;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        DestinationType = destinationType;
        RequestedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;

        if (autoApprove)
        {
            Status = WithdrawalStatus.Approved;
            ProcessedAt = DateTime.UtcNow;
        }
        else
        {
            Status = WithdrawalStatus.Pending;
        }
    }

    public WithdrawalRequest(
        Guid driverId,
        Guid walletId,
        decimal amount,
        string bankAccountNumber,
        string bankName,
        string accountHolderName,
        bool autoApprove = true,
        string? idempotencyKey = null,
        WithdrawalDestinationType destinationType = WithdrawalDestinationType.BankAccount)
    {
        DriverId = driverId;
        WalletId = walletId;
        Amount = amount;
        BankAccountNumber = bankAccountNumber;
        BankName = bankName;
        AccountHolderName = accountHolderName;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        DestinationType = destinationType;
        RequestedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;

        if (autoApprove)
        {
            Status = WithdrawalStatus.Approved;
            ProcessedAt = DateTime.UtcNow;
        }
        else
        {
            Status = WithdrawalStatus.Pending;
        }
    }

    public void Approve(Guid processedByUserId)
    {
        if (Status != WithdrawalStatus.Pending)
            throw new InvalidOperationException("Only pending withdrawals can be approved");

        Status = WithdrawalStatus.Approved;
        ProcessedByUserId = processedByUserId;
        ProcessedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Auto-approve withdrawal (for independent drivers)
    /// </summary>
    public void AutoApprove()
    {
        if (Status != WithdrawalStatus.Pending)
            throw new InvalidOperationException("Only pending withdrawals can be auto-approved");

        Status = WithdrawalStatus.Approved;
        ProcessedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reject(Guid processedByUserId, string reason)
    {
        if (Status != WithdrawalStatus.Pending)
            throw new InvalidOperationException("Only pending withdrawals can be rejected");

        Status = WithdrawalStatus.Rejected;
        ProcessedByUserId = processedByUserId;
        ProcessedAt = DateTime.UtcNow;
        RejectionReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsCompleted()
    {
        if (Status != WithdrawalStatus.Approved)
            throw new InvalidOperationException("Only approved withdrawals can be marked as completed");

        Status = WithdrawalStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetProviderDisbursement(string provider, string disbursementId)
    {
        Provider = provider;
        ProviderDisbursementId = disbursementId;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Records the QR the payout was executed against, so reconciliation can poll it.</summary>
    public void SetQrId(string? qrId)
    {
        QrId = string.IsNullOrWhiteSpace(qrId) ? null : qrId.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Terminal failure. Valid from Approved (the provider accepted, then the rail rejected)
    /// and from Pending (funds were reserved, but the provider never accepted the transfer).
    /// Rejected/Completed are already terminal and must not be reopened.
    /// </summary>
    public void MarkAsFailed(string reason)
    {
        if (Status is not (WithdrawalStatus.Pending or WithdrawalStatus.Approved))
            throw new InvalidOperationException($"Cannot fail a withdrawal in status {Status}");

        Status = WithdrawalStatus.Failed;
        RejectionReason = reason;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// How the driver nominated the destination. QR Ph carries no account number — the
/// scanned code identifies the receiving account — so the bank fields hold display
/// text only when this is <see cref="WithdrawalDestinationType.QrPh"/>.
/// </summary>
public enum WithdrawalDestinationType
{
    BankAccount = 0,
    QrPh = 1
}

public enum WithdrawalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Completed = 3,
    Failed = 4
}

