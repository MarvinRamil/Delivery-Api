using BeeLogistics.Modules.Drivers.Domain;

namespace BeeLogistics.Modules.Drivers.Application.DTOs;

public record DriverWalletDto(
    Guid Id,
    Guid DriverId,
    decimal PersonalBalance,
    decimal TopUpBalance,
    decimal PendingPayout,
    bool CanAcceptCashJobs,
    string? BankAccountNumber,
    string? BankName,
    string? AccountHolderName,
    DateTime LastUpdatedAt
);

public record WalletTransactionDto(
    Guid Id,
    Guid WalletId,
    WalletTransactionType Type,
    WalletBucket Bucket,
    decimal Amount,
    WalletTransactionStatus Status,
    string Description,
    Guid? RelatedBookingId,
    Guid? RelatedWithdrawalRequestId,
    DateTime TransactionDate
);

public record WithdrawalRequestDto(
    Guid Id,
    Guid DriverId,
    Guid WalletId,
    decimal Amount,
    WithdrawalStatus Status,
    string BankAccountNumber,
    string BankName,
    string AccountHolderName,
    string? RejectionReason,
    DateTime RequestedAt,
    DateTime? ProcessedAt,
    Guid? ProcessedByUserId
);

public record CreateWithdrawalRequestDto(
    decimal Amount,
    Guid? SavedWithdrawalMethodId = null, // Optional: use saved withdrawal method
    string? BankAccountNumber = null, // Required if SavedWithdrawalMethodId is not provided
    string? BankName = null, // Required if SavedWithdrawalMethodId is not provided
    string? AccountHolderName = null, // Required if SavedWithdrawalMethodId is not provided
    string? BankCode = null, // Catalog code from GET /api/payments/banks; older builds omit it and BankName is tried instead
    string? IdempotencyKey = null, // Optional: duplicate requests with same key return existing withdrawal
    WithdrawalDestinationType DestinationType = WithdrawalDestinationType.BankAccount,
    string? QrString = null // Required when DestinationType is QrPh; the raw scanned QR Ph payload
);

public record CreateDriverTopUpRequestDto(
    decimal Amount,
    string? PayerEmail = null,
    string? Description = null,
    string? IdempotencyKey = null
);

public record DriverTopUpDto(
    Guid Id,
    Guid DriverId,
    Guid WalletId,
    decimal Amount,
    DriverTopUpStatus Status,
    string ExternalId,
    string? IdempotencyKey,
    string? XenditInvoiceId,
    string? XenditInvoiceUrl,
    DateTime? ExpiresAt,
    DateTime? PaidAt,
    DateTime? CreditedAt,
    DateTime CreatedAt
);

public record CancelDriverTopUpRequestDto(string? Reason = null);

public record TransferWalletBalanceRequestDto(
    WalletBucket From,
    WalletBucket To,
    decimal Amount
);

public record CashJobEligibilityDto(
    bool CanAcceptCashJobs,
    decimal CurrentTopUpBalance,
    decimal BlockThreshold,
    decimal AllowedNegativeLimit
);
/// <summary>
/// Where a driver is in PayMongo child-account setup (issue #91).
/// <para>
/// <paramref name="VerificationUrl"/> is only present on the response that issues a session; it is
/// deliberately not stored or replayed, since sessions expire in ~72 hours and a stale link looks
/// to a driver exactly like a broken one.
/// </para>
/// </summary>
public record PayMongoOnboardingDto(
    string Status,
    string? AccountId,
    // The address the account was opened under. Not always the driver's plain email: if theirs
    // was already registered with PayMongo it becomes a plus-tagged variant, which still reaches
    // the same inbox. Frozen at activation, so support needs to see which one it carries.
    string? AccountEmail,
    string? WalletAccountNumber,
    string? VerificationUrl,
    DateTime? VerificationExpiresAt,
    // Why the last identity check failed, in terms the driver can act on. Usually a retake away
    // from passing, so it must reach them rather than leaving a stuck screen.
    string? VerificationFailureReason,
    bool WalletReady
);

/// <summary>
/// Details PayMongo requires before a child account can be activated.
/// <para>
/// Country is not accepted from the client: these accounts are Philippine consumer accounts and
/// PayMongo derives eligibility from that, so it is fixed server-side. <c>AddressState</c> is an
/// ISO 3166-2 subdivision code such as "PH-ILN", not a province name.
/// </para>
/// </summary>
public record SubmitPayMongoOnboardingDetailsDto(
    string Nationality,
    string NatureOfWork,
    string SourceOfFunds,
    string Tin,
    string PlaceOfBirthCity,
    string AddressLine1,
    string AddressCity,
    string AddressState,
    string AddressPostalCode,
    string? MiddleName = null,
    string? SourceOfFundsOther = null,
    // Optional: falls back to the profile. Lets a driver with no phone on file supply one here
    // rather than being blocked at activation.
    string? MobileNumber = null
);

/// <summary>
/// What a driver can withdraw, and why it differs from their balance.
/// </summary>
/// <param name="Fee">
/// Estimated transfer fee. An upper bound used to cap the field — the amount actually charged is
/// read back off the transfer, because it has been observed to vary.
/// </param>
/// <param name="FeePaidByDriver">
/// True once the driver is on their own PayMongo wallet, where the fee leaves their balance. False
/// on the original path, where the platform absorbs it.
/// </param>
public record WithdrawableBalanceDto(
    decimal Balance,
    decimal Withdrawable,
    decimal Fee,
    bool FeePaidByDriver
);

/// <summary>
/// A QR the driver scans to put money into their own BeeWallet wallet.
/// </summary>
/// <param name="QrString">
/// The EMV payload to render. Credits land in real time over InstaPay rather than waiting on
/// payment settlement, which is the whole reason this exists.
/// </param>
/// <param name="ExpiresAt">Null for a static QR — it belongs to the driver, not to one payment.</param>
public record WalletTopUpQrDto(
    string QrString,
    // The same payload rendered as a PNG data URI, so the app can show it with <Image> instead of
    // taking a native QR-rendering dependency and the rebuild that comes with it.
    string QrImage,
    string? MerchantName,
    string? AccountNumber,
    DateTime? ExpiresAt,
    // Echoed back so the app can show what the payer will be charged, and tell a fixed-amount code
    // apart from the reusable one without re-reading the request. Null for a static QR.
    decimal? Amount = null
);
