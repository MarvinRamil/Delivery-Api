using BeeLogistics.Modules.Drivers.Domain;

namespace BeeLogistics.Modules.Drivers.Application.Interfaces;

public interface IDriverWalletRepository
{
    Task<DriverWallet?> GetWalletByDriverIdAsync(Guid driverId, CancellationToken ct = default);

    /// <summary>Finds the wallet linked to a PayMongo child account, for webhook routing.</summary>
    Task<DriverWallet?> GetWalletByPayMongoAccountIdAsync(string payMongoAccountId, CancellationToken ct = default);
    /// <summary>
    /// Finds the wallet by its PayMongo account number — the <c>credit_account_number</c> a
    /// <c>qr.paid</c> webhook names. More direct than the account id, which the payload carries
    /// only in a prefixed and an unprefixed form.
    /// </summary>
    Task<DriverWallet?> GetWalletByPayMongoAccountNumberAsync(string accountNumber, CancellationToken ct = default);

    Task<DriverWallet> CreateWalletAsync(DriverWallet wallet, CancellationToken ct = default);
    Task<DriverWallet> UpdateWalletAsync(DriverWallet wallet, CancellationToken ct = default);
    Task<List<WalletTransaction>> GetTransactionsByWalletIdAsync(Guid walletId, DateTime? startDate = null, DateTime? endDate = null, WalletTransactionType? type = null, CancellationToken ct = default);
    Task<WalletTransaction> CreateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Whether this wallet already has a transaction of <paramref name="type"/> for the given
    /// provider payment. Backed by the unique index on (WalletId, Type, ProviderPaymentId).
    /// </summary>
    Task<bool> HasTransactionForProviderPaymentAsync(Guid walletId, string providerPaymentId, WalletTransactionType type, CancellationToken ct = default);

    /// <summary>
    /// Whether this wallet already has a transaction of <paramref name="type"/> for the booking.
    /// Backed by the unique index on (WalletId, RelatedBookingId, Type).
    /// </summary>
    Task<bool> HasTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default);

    /// <summary>
    /// That transaction itself, or null. Same index as
    /// <see cref="HasTransactionForBookingAsync"/> — used where the caller needs the row's status,
    /// not just whether it exists, so a claim written before a failed transfer can be retried
    /// rather than counted as done.
    /// </summary>
    Task<WalletTransaction?> GetTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default);

    /// <summary>
    /// Writes the transaction row and the updated wallet balance in a single SaveChanges, so a
    /// failure can never leave one without the other.
    /// </summary>
    Task ApplyTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Commits a balance change alongside an <b>already-persisted</b> transaction row, in one save.
    /// <para>
    /// Distinct from <see cref="ApplyTransactionAsync"/>, which inserts. Used by the claim-first
    /// ordering: the row is written before the provider call so a redelivery collides on the unique
    /// index, and is then updated - not re-inserted - once the outcome is known.
    /// </para>
    /// </summary>
    Task SaveClaimedTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Writes both legs of a Personal/Top-up transfer and the updated wallet in a single
    /// SaveChanges. The two buckets move together in memory, so the ledger rows recording that
    /// move have to commit together too - otherwise the stored balance and the balance
    /// <see cref="GetCalculatedPersonalBalanceAsync"/> reconstructs from transactions diverge
    /// permanently.
    /// </summary>
    Task SaveWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default);

    /// <summary>
    /// Whether the wallet has any transaction still <see cref="WalletTransactionStatus.Pending"/>.
    /// <para>
    /// Used to hold reconciliation off a wallet that is mid-operation. A claim-before-send row is
    /// written before the provider call, so between the two the local and provider views legitimately
    /// disagree — correcting that gap would be correcting a difference that is about to close itself.
    /// </para>
    /// </summary>
    Task<bool> HasPendingTransactionsAsync(Guid walletId, CancellationToken ct = default);

    /// <summary>
    /// Platform commission the driver still owes: their PlatformCommission rows that never
    /// completed, oldest first (issue #102).
    /// </summary>
    /// <remarks>
    /// The rows <i>are</i> the debt — there is no balance column holding it — so this is both how
    /// arrears are found for a retry and how "what do they owe" is answered. Returning the rows
    /// rather than a total lets the caller re-sweep each one against the transfer it belongs to.
    /// </remarks>
    Task<IReadOnlyList<WalletTransaction>> GetUnpaidCommissionsAsync(Guid walletId, CancellationToken ct = default);

    /// <summary>Whether this wallet owes any platform commission.</summary>
    Task<bool> HasUnpaidCommissionAsync(Guid walletId, CancellationToken ct = default);

    /// <summary>
    /// A cashbond payment already in flight for this wallet, if there is one.
    /// </summary>
    /// <remarks>
    /// The retry guard. <c>DriverWallet.HasPaidCashBond</c> only becomes true on a
    /// <c>Succeeded</c> provider response, so a payment sitting at <c>pending</c> leaves it false
    /// and every further attempt looked like a first attempt. On 2026-09-01 that took ₱30 out of
    /// one driver's wallet for a ₱10 cashbond — three real transfers fired 15 seconds apart,
    /// all three settled, and the driver was still shown as unpaid.
    /// </remarks>
    Task<WalletTransaction?> GetPendingCashBondPaymentAsync(Guid walletId, CancellationToken ct = default);

    /// <summary>
    /// A cashbond payment by its own id, with the wallet it belongs to.
    /// </summary>
    /// <remarks>
    /// How a cashbond <c>qr.paid</c> webhook is routed. The QR credits the platform wallet, so the
    /// payload carries no child account number or org id to identify the driver by — the reference
    /// label is the only link, and it embeds this id.
    /// </remarks>
    Task<(DriverWallet Wallet, WalletTransaction Transaction)?> GetCashBondPaymentWithWalletAsync(
        Guid transactionId, CancellationToken ct = default);

    // ── Package insurance (issue #104) ──────────────────────────────────────────────────────

    /// <summary>This driver's package-insurance policy row, if one has ever been created.</summary>
    Task<DriverPackageInsurancePolicy?> GetInsurancePolicyByDriverIdAsync(Guid driverId, CancellationToken ct = default);

    Task<DriverPackageInsurancePolicy> CreateInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default);

    /// <summary>
    /// A package-insurance premium payment already in flight for this wallet and policy year, if
    /// there is one. Same retry guard as <see cref="GetPendingCashBondPaymentAsync"/> — read
    /// before claiming, so a re-issued QR reuses the same transaction row instead of opening a
    /// second one.
    /// </summary>
    Task<WalletTransaction?> GetPendingInsurancePremiumPaymentAsync(Guid walletId, int policyYearNumber, CancellationToken ct = default);

    /// <summary>
    /// A package-insurance premium payment by its own id, with the wallet and policy it belongs
    /// to. How a <c>qr.paid</c> webhook is routed — same reasoning as
    /// <see cref="GetCashBondPaymentWithWalletAsync"/>.
    /// </summary>
    Task<(DriverWallet Wallet, DriverPackageInsurancePolicy Policy, WalletTransaction Transaction)?> GetInsurancePremiumPaymentWithPolicyAsync(
        Guid transactionId, CancellationToken ct = default);

    /// <summary>
    /// Commits a settled premium payment: the policy's paid-through year and the transaction row,
    /// in one SaveChanges. This is where <see cref="DriverPackageInsurancePolicy.Version"/>'s
    /// xmin check fires — two concurrent settlements of the same policy year, one wins here.
    /// </summary>
    Task SaveClaimedInsurancePremiumAsync(DriverPackageInsurancePolicy policy, WalletTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Active policies, for the daily expiry sweep. Paged by id so a growing fleet does not load
    /// every policy into memory at once. The caller filters by <c>CoverageEndDate</c> — a
    /// computed property, not a mapped column, so it can't be filtered in SQL here.
    /// </summary>
    Task<IReadOnlyList<DriverPackageInsurancePolicy>> GetActiveInsurancePoliciesAsync(Guid? afterId, int limit, CancellationToken ct = default);

    Task SaveInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Which of these wallets owe platform commission. The batched form, for building a list of
    /// wallets without a query each.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetWalletIdsWithUnpaidCommissionAsync(IReadOnlyCollection<Guid> walletIds, CancellationToken ct = default);

    /// <summary>
    /// Of the given drivers, those who can be offered a cash job right now: they hold their balance
    /// at PayMongo, that balance covers <paramref name="requiredCommission"/>, and they owe nothing
    /// from an earlier job.
    /// </summary>
    /// <remarks>
    /// Takes the whole candidate set and answers in one round trip. This sits on the dispatch path,
    /// where a query per candidate would put the wallet check in the way of every booking going out.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetDriversEligibleForCashJobAsync(
        IReadOnlyCollection<Guid> driverIds, decimal requiredCommission, CancellationToken ct = default);

    /// <summary>
    /// Commits a balance change alongside <b>already-persisted</b> transfer legs, in one save.
    /// <para>
    /// The two-leg equivalent of <see cref="SaveClaimedTransactionAsync"/>: used when the legs were
    /// written as Pending before an irreversible provider call, so they must be updated rather than
    /// re-inserted once the outcome is known.
    /// </para>
    /// </summary>
    Task SaveClaimedWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default);

    /// <summary>
    /// Wallet-transfer legs still Pending against a provider transfer, oldest first.
    /// <para>
    /// Returns the pair for each transfer, keyed by its provider id. Only legs that carry one are
    /// returned: without an id there is nothing to ask the provider about.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<(DriverWallet Wallet, WalletTransaction Outbound, WalletTransaction Inbound)>>
        GetPendingWalletTransfersAsync(DateTime createdBefore, int limit, CancellationToken ct = default);

    Task<decimal> GetCalculatedPersonalBalanceAsync(Guid walletId, CancellationToken ct = default);
    Task<List<WithdrawalRequest>> GetWithdrawalRequestsByDriverIdAsync(Guid driverId, WithdrawalStatus? status = null, CancellationToken ct = default);
    Task<WithdrawalRequest?> GetWithdrawalByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default);
    Task<WithdrawalRequest?> GetWithdrawalRequestByIdAsync(Guid requestId, CancellationToken ct = default);
    /// <summary>Looks up by provider payout/transfer id, scoped to the provider so ids can never collide across gateways.</summary>
    Task<WithdrawalRequest?> GetWithdrawalByProviderDisbursementIdAsync(string provider, string providerDisbursementId, CancellationToken ct = default);
    Task<WalletTransaction?> GetWithdrawalTransactionByRequestIdAsync(Guid withdrawalRequestId, CancellationToken ct = default);
    Task<WithdrawalRequest> CreateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default);
    Task<WithdrawalRequest> UpdateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default);
    Task UpdateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default);
    /// <summary>
    /// Commits a withdrawal reservation: wallet debit + withdrawal row + ledger row in one
    /// transaction. Throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
    /// when another withdrawal for the same wallet won the race.
    /// </summary>
    Task SaveWithdrawalReservationAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default);

    /// <summary>
    /// Reservations that were debited but never got a provider id back. The provider may or
    /// may not have created the transfer, so these are resolved by reference, never refunded
    /// blindly.
    /// </summary>
    Task<IReadOnlyList<WithdrawalRequest>> GetUnconfirmedReservationsAsync(DateTime requestedBefore, int limit, CancellationToken ct = default);

    Task SaveWithdrawalDisbursementResultAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default);
    /// <summary>Persists top-up credit (topUp + wallet; optional new transaction) in a single SaveChanges for atomicity.</summary>
    Task SaveTopUpCreditAsync(DriverTopUp topUp, DriverWallet wallet, WalletTransaction? transaction = null, CancellationToken ct = default);
    Task<DriverTopUp> CreateTopUpAsync(DriverTopUp topUp, CancellationToken ct = default);
    Task<DriverTopUp?> GetTopUpByIdAsync(Guid topUpId, CancellationToken ct = default);
    Task<DriverTopUp?> GetTopUpByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default);
    /// <summary>Looks up by provider checkout id, scoped to the provider so ids can never collide across gateways.</summary>
    Task<DriverTopUp?> GetTopUpByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default);
    Task<DriverTopUp?> GetTopUpByExternalIdAsync(string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<DriverTopUp>> GetTopUpsByDriverIdAsync(Guid driverId, CancellationToken ct = default);
    Task<IReadOnlyList<DriverTopUp>> GetTopUpsAsync(DateTime? from = null, DateTime? to = null, DriverTopUpStatus? status = null, CancellationToken ct = default);

    /// <summary>
    /// Pending top-ups created before <paramref name="createdBefore"/>, **oldest first**, that
    /// actually have a provider checkout to ask about.
    /// </summary>
    /// <remarks>
    /// The ordering is the point. The pending pool is every abandoned checkout of the last day,
    /// so it routinely exceeds any sane per-run cap; taking the newest would re-check the same
    /// recent records forever and never reach the genuinely stuck ones. Filtering
    /// ProviderPaymentId in SQL rather than skipping in the loop keeps the cap spent on
    /// candidates that can actually be resolved.
    /// </remarks>
    /// <summary>
    /// Withdrawals the provider accepted but never reported a final status for. Oldest first,
    /// for the same reason as the top-up query: newest-first starves the stuck records.
    /// </summary>
    Task<IReadOnlyList<WithdrawalRequest>> GetWithdrawalsAwaitingReconciliationAsync(DateTime requestedBefore, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<DriverTopUp>> GetTopUpsAwaitingReconciliationAsync(DateTime createdBefore, int limit, CancellationToken ct = default);

    /// <summary>
    /// Top-ups already closed as Expired that were never credited, oldest first, created no
    /// earlier than <paramref name="createdSince"/>.
    /// </summary>
    /// <remarks>
    /// The expiry job closes records on a local timer without asking the provider, and ordinary
    /// reconciliation only looks at Pending. Without this, a top-up the driver genuinely paid for
    /// is written off permanently. Oldest first so records about to age out of the window get
    /// their last check.
    /// </remarks>
    Task<IReadOnlyList<DriverTopUp>> GetUncreditedExpiredTopUpsAsync(DateTime createdSince, int limit, CancellationToken ct = default);
    /// <summary>
    /// Pending top-ups that should be auto-closed: either ExpiresAt has passed, or (no ExpiresAt) and created longer than maxAgeWithoutExpiry ago.
    /// </summary>
    Task<IReadOnlyList<DriverTopUp>> GetPendingTopUpsToExpireAsync(DateTime utcNow, TimeSpan maxAgeWithoutExpiry, CancellationToken ct = default);
    Task<IReadOnlyList<DriverWallet>> GetWalletsBelowTopUpThresholdAsync(decimal threshold, CancellationToken ct = default);

    /// <summary>
    /// Wallets with an activated PayMongo child account, for the monthly upkeep charge.
    /// Paged by id so a growing fleet does not load every wallet into memory at once.
    /// </summary>
    Task<IReadOnlyList<DriverWallet>> GetActivatedPayMongoWalletsAsync(Guid? afterId, int limit, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<List<DriverMission>> GetMissionsByDriverIdAsync(Guid driverId, MissionStatus? status = null, CancellationToken ct = default);
    Task<DriverMission?> GetMissionByIdAsync(Guid missionId, CancellationToken ct = default);
    Task<DriverMission> CreateMissionAsync(DriverMission mission, CancellationToken ct = default);
}
