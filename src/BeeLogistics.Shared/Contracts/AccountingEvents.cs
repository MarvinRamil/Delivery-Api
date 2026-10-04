namespace BeeLogistics.Shared.Contracts;

/// <summary>
/// Published when a driver withdrawal is requested (wallet debited, payout pending).
/// Accounting module consumes this to record ledger entries (non-blocking).
/// Optional receipt fields: when set, a withdrawal receipt email is sent to the driver.
/// </summary>
public sealed record WithdrawalRequestedEvent(
    Guid WithdrawalId,
    Guid DriverId,
    Guid WalletId,
    decimal Amount,
    string Currency,
    DateTime RequestedAtUtc,
    string? IdempotencyKey,
    string? DriverEmail = null,
    string? DriverName = null,
    string? ProviderPayoutId = null,
    string? MaskedAccountNumber = null,
    string? BankName = null
);

/// <summary>
/// Published when a withdrawal disbursement is completed (money sent to driver's bank).
/// Accounting module consumes to record completion (debit PendingPayout, credit XenditOut).
/// </summary>
public sealed record WithdrawalCompletedEvent(
    Guid WithdrawalId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    DateTime CompletedAtUtc
);

/// <summary>
/// Published when a withdrawal disbursement failed (rollback applied; wallet recredited).
/// Accounting module consumes to record reversal entries.
/// </summary>
public sealed record WithdrawalFailedEvent(
    Guid WithdrawalId,
    Guid DriverId,
    decimal Amount,
    string Currency,
    string Reason,
    DateTime FailedAtUtc
);

/// <summary>
/// Published when a booking sale is completed (cash or cashless).
/// Accounting module consumes to record ledger entries and the fast Sales table.
/// </summary>
public sealed record SaleRecordedEvent(
    Guid BookingId,
    Guid DriverId,
    Guid CustomerId,
    decimal Amount,
    string PaymentMethod,
    decimal PlatformCommissionAmount,
    decimal DriverAmount,
    DateTime CompletedAtUtc
);

/// <summary>
/// Published when a driver's TopUp wallet is debited to settle the platform's commission on a
/// cash-collected delivery. Accounting module consumes to record ledger entries (debit
/// DriverTopUpWallet, credit CustomerPayment) - previously this real wallet debit had no ledger
/// representation at all, so AccountCode.DriverTopUpWallet's reported balance was always zero.
/// </summary>
public sealed record CashSettlementDebitedEvent(
    Guid BookingId,
    Guid DriverId,
    Guid WalletId,
    decimal Amount,
    DateTime DebitedAtUtc
);

/// <summary>
/// Published when a driver's TopUp wallet is credited from a completed top-up checkout (real cash
/// received from the driver via Xendit/PayMongo). Accounting module consumes to record ledger
/// entries (debit DriverTopUpPayment, credit DriverTopUpWallet).
/// </summary>
public sealed record DriverTopUpCreditedEvent(
    Guid TopUpId,
    Guid DriverId,
    Guid WalletId,
    decimal Amount,
    string Provider,
    DateTime CreditedAtUtc
);

/// <summary>
/// Published when an admin manually adjusts a driver's TopUp wallet for a cash-collection dispute
/// (WalletTransactionType.CashDeficitAdjustment). Accounting module consumes to record ledger
/// entries against a suspense account, keeping every wallet-balance change ledger-covered.
/// </summary>
public sealed record CashDeficitAdjustedEvent(
    Guid AdjustmentId,
    Guid DriverId,
    Guid WalletId,
    decimal SignedAmount,
    string Reason,
    string PerformedBy,
    DateTime AdjustedAtUtc
);
