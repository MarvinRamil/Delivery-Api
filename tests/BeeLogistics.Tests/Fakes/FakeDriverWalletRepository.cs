using Microsoft.EntityFrameworkCore;
using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;

namespace BeeLogistics.Tests.Fakes;

/// <summary>
/// In-memory IDriverWalletRepository covering the wallet-crediting paths.
///
/// Enforces the same unique constraint as the database — (WalletId, RelatedBookingId, Type) among
/// non-deleted rows — so a test that would double-credit fails here rather than only in
/// production. <see cref="FailNextApplyTransaction"/> simulates the mid-consumer failure that the
/// old "marker committed before the effect" ordering turned into permanent data loss.
/// </summary>
public sealed class FakeDriverWalletRepository : IDriverWalletRepository
{
    public List<DriverWallet> Wallets { get; } = new();
    public List<WalletTransaction> Transactions { get; } = new();
    public List<WithdrawalRequest> WithdrawalRequests { get; } = new();

    /// <summary>When set, the next ApplyTransactionAsync throws instead of writing.</summary>
    public bool FailNextApplyTransaction { get; set; }

    /// <summary>
    /// When set, the next SaveWithdrawalReservationAsync throws DbUpdateConcurrencyException,
    /// standing in for another device winning the xmin race on the wallet row.
    /// </summary>
    public bool FailNextReservationWithConcurrency { get; set; }

    /// <summary>Reservations committed through SaveWithdrawalReservationAsync, in order.</summary>
    public List<WithdrawalRequest> Reservations { get; } = new();

    public Task<DriverWallet?> GetWalletByDriverIdAsync(Guid driverId, CancellationToken ct = default)
        => Task.FromResult(Wallets.FirstOrDefault(w => w.DriverId == driverId));

    public Task<IReadOnlyList<DriverWallet>> GetActivatedPayMongoWalletsAsync(Guid? afterId, int limit, CancellationToken ct = default)
    {
        // Mirrors the real query: keyset-paged by id so a test can exercise more than one page.
        IEnumerable<DriverWallet> q = Wallets
            .Where(w => w.PayMongoActivationStatus == BeeLogistics.Modules.Payment.Application.Gateways.PayMongoActivationStatus.Activated)
            .OrderBy(w => w.Id);
        if (afterId.HasValue) q = q.Where(w => w.Id > afterId.Value);
        return Task.FromResult<IReadOnlyList<DriverWallet>>(q.Take(limit).ToList());
    }

    public Task<DriverWallet?> GetWalletByPayMongoAccountIdAsync(string payMongoAccountId, CancellationToken ct = default)
        => Task.FromResult(Wallets.FirstOrDefault(w => w.PayMongoAccountId == payMongoAccountId));

    public Task<DriverWallet?> GetWalletByPayMongoAccountNumberAsync(string accountNumber, CancellationToken ct = default)
        => Task.FromResult(Wallets.FirstOrDefault(w => w.PayMongoAccountNumber == accountNumber));

    public Task<DriverWallet> CreateWalletAsync(DriverWallet wallet, CancellationToken ct = default)
    {
        Wallets.Add(wallet);
        return Task.FromResult(wallet);
    }

    public Task<DriverWallet> UpdateWalletAsync(DriverWallet wallet, CancellationToken ct = default)
        => Task.FromResult(wallet);

    public Task<WalletTransaction> CreateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default)
    {
        GuardUnique(transaction);
        Transactions.Add(transaction);
        return Task.FromResult(transaction);
    }

    public Task ApplyTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        if (FailNextApplyTransaction)
        {
            FailNextApplyTransaction = false;
            // Nothing is recorded: the real implementation writes the row and the balance in one
            // SaveChanges, so a failure leaves neither.
            throw new InvalidOperationException("Simulated failure while applying wallet transaction.");
        }

        GuardUnique(transaction);
        Transactions.Add(transaction);
        return Task.CompletedTask;
    }

    /// <summary>When set, the next SaveClaimedTransactionAsync throws instead of writing.</summary>
    public bool FailNextSaveClaimedTransaction { get; set; }

    public Task SaveClaimedTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        if (FailNextSaveClaimedTransaction)
        {
            FailNextSaveClaimedTransaction = false;
            throw new InvalidOperationException("save failed");
        }

        // Deliberately does NOT call GuardUnique: the row is already claimed, and re-checking would
        // collide it with itself - which is exactly what the real repository avoids by using Update
        // rather than Add.
        if (!Transactions.Contains(transaction)) Transactions.Add(transaction);
        return Task.CompletedTask;
    }

    public Task SaveWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default)
    {
        // Both legs are guarded before either is recorded, so a clash on the second leg cannot
        // leave the first one behind - matching the single SaveChanges in the real repository.
        GuardUnique(outbound);
        GuardUnique(inbound);
        Transactions.Add(outbound);
        Transactions.Add(inbound);
        return Task.CompletedTask;
    }

    public Task<bool> HasTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default)
        => Task.FromResult(Transactions.Any(t =>
            t.WalletId == walletId && t.RelatedBookingId == bookingId && t.Type == type && !t.IsDeleted));

    public Task<WalletTransaction?> GetTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default)
        => Task.FromResult(Transactions.FirstOrDefault(t =>
            t.WalletId == walletId && t.RelatedBookingId == bookingId && t.Type == type && !t.IsDeleted));

    public Task<IReadOnlyList<WalletTransaction>> GetUnpaidCommissionsAsync(Guid walletId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WalletTransaction>>(Transactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            .OrderBy(t => t.TransactionDate)
            .ToList());

    public Task<WalletTransaction?> GetPendingCashBondPaymentAsync(Guid walletId, CancellationToken ct = default)
        => Task.FromResult(Transactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.CashBondPayment
                        && t.Status == WalletTransactionStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault());

    public Task<(DriverWallet Wallet, WalletTransaction Transaction)?> GetCashBondPaymentWithWalletAsync(
        Guid transactionId, CancellationToken ct = default)
    {
        var transaction = Transactions.FirstOrDefault(
            t => t.Id == transactionId && !t.IsDeleted && t.Type == WalletTransactionType.CashBondPayment);
        if (transaction is null) return Task.FromResult<(DriverWallet, WalletTransaction)?>(null);

        var wallet = Wallets.FirstOrDefault(w => w.Id == transaction.WalletId);
        return Task.FromResult(wallet is null ? null : ((DriverWallet, WalletTransaction)?)(wallet, transaction));
    }

    // --- Package insurance (issue #104) ---

    public List<DriverPackageInsurancePolicy> InsurancePolicies { get; } = new();

    public Task<DriverPackageInsurancePolicy?> GetInsurancePolicyByDriverIdAsync(Guid driverId, CancellationToken ct = default)
        => Task.FromResult(InsurancePolicies.FirstOrDefault(p => p.DriverId == driverId));

    public Task<DriverPackageInsurancePolicy> CreateInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default)
    {
        InsurancePolicies.Add(policy);
        return Task.FromResult(policy);
    }

    public Task<WalletTransaction?> GetPendingInsurancePremiumPaymentAsync(Guid walletId, int policyYearNumber, CancellationToken ct = default)
        => Task.FromResult(Transactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PackageInsurancePayment
                        && t.PolicyYearNumber == policyYearNumber
                        && t.Status == WalletTransactionStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault());

    public Task<(DriverWallet Wallet, DriverPackageInsurancePolicy Policy, WalletTransaction Transaction)?> GetInsurancePremiumPaymentWithPolicyAsync(
        Guid transactionId, CancellationToken ct = default)
    {
        var transaction = Transactions.FirstOrDefault(
            t => t.Id == transactionId && !t.IsDeleted && t.Type == WalletTransactionType.PackageInsurancePayment);
        if (transaction is null)
            return Task.FromResult<(DriverWallet, DriverPackageInsurancePolicy, WalletTransaction)?>(null);

        var wallet = Wallets.FirstOrDefault(w => w.Id == transaction.WalletId);
        if (wallet is null)
            return Task.FromResult<(DriverWallet, DriverPackageInsurancePolicy, WalletTransaction)?>(null);

        var policy = InsurancePolicies.FirstOrDefault(p => p.DriverId == wallet.DriverId);
        return Task.FromResult(policy is null
            ? null
            : ((DriverWallet, DriverPackageInsurancePolicy, WalletTransaction)?)(wallet, policy, transaction));
    }

    /// <summary>When set, the next SaveClaimedInsurancePremiumAsync throws DbUpdateConcurrencyException.</summary>
    public bool FailNextClaimedInsurancePremiumWithConcurrency { get; set; }

    public Task SaveClaimedInsurancePremiumAsync(DriverPackageInsurancePolicy policy, WalletTransaction transaction, CancellationToken ct = default)
    {
        if (FailNextClaimedInsurancePremiumWithConcurrency)
        {
            FailNextClaimedInsurancePremiumWithConcurrency = false;
            throw new DbUpdateConcurrencyException("Simulated: another settlement won the race for this policy year.");
        }

        if (!InsurancePolicies.Contains(policy)) InsurancePolicies.Add(policy);
        if (!Transactions.Contains(transaction)) Transactions.Add(transaction);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DriverPackageInsurancePolicy>> GetActiveInsurancePoliciesAsync(Guid? afterId, int limit, CancellationToken ct = default)
    {
        IEnumerable<DriverPackageInsurancePolicy> q = InsurancePolicies
            .Where(p => p.Status == DriverPackageInsurancePolicyStatus.Active)
            .OrderBy(p => p.Id);
        if (afterId.HasValue) q = q.Where(p => p.Id > afterId.Value);
        return Task.FromResult<IReadOnlyList<DriverPackageInsurancePolicy>>(q.Take(limit).ToList());
    }

    public Task SaveInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default)
    {
        if (!InsurancePolicies.Contains(policy)) InsurancePolicies.Add(policy);
        return Task.CompletedTask;
    }

    public Task<bool> HasUnpaidCommissionAsync(Guid walletId, CancellationToken ct = default)
        => Task.FromResult(Transactions.Any(t =>
            t.WalletId == walletId
            && !t.IsDeleted
            && t.Type == WalletTransactionType.PlatformCommission
            && t.Status == WalletTransactionStatus.Pending));

    public Task<IReadOnlySet<Guid>> GetWalletIdsWithUnpaidCommissionAsync(IReadOnlyCollection<Guid> walletIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<Guid>>(Transactions
            .Where(t => walletIds.Contains(t.WalletId)
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            .Select(t => t.WalletId)
            .ToHashSet());

    public Task<IReadOnlyList<Guid>> GetDriversEligibleForCashJobAsync(
        IReadOnlyCollection<Guid> driverIds, decimal requiredCommission, CancellationToken ct = default)
    {
        var owing = Transactions
            .Where(t => !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            .Select(t => t.WalletId)
            .ToHashSet();

        return Task.FromResult<IReadOnlyList<Guid>>(Wallets
            .Where(w => driverIds.Contains(w.DriverId)
                        && w.UsesPayMongoWallet
                        && w.Balance >= requiredCommission
                        && !owing.Contains(w.Id)
                        // Cashbond gate (issue #103), mirroring DriverWalletRepository.
                        && w.CashBondBalance > 0)
            .Select(w => w.DriverId)
            .ToList());
    }

    private void GuardUnique(WalletTransaction transaction)
    {
        // (WalletId, RelatedBookingId, Type) — booking-linked rows.
        if (transaction.RelatedBookingId is not null)
        {
            var bookingClash = Transactions.Any(t =>
                !t.IsDeleted &&
                t.WalletId == transaction.WalletId &&
                t.RelatedBookingId == transaction.RelatedBookingId &&
                t.Type == transaction.Type);

            if (bookingClash)
                throw new InvalidOperationException(
                    $"Unique index violation: a {transaction.Type} transaction already exists for wallet "
                    + $"{transaction.WalletId} and booking {transaction.RelatedBookingId}.");
        }

        // (WalletId, Type, ProviderPaymentId) — provider-driven rows (GitLab #66). Top-ups carry
        // no booking id, so the index above never covered them: their only guard was a string
        // comparison on the description. Enforced here so a test that would double-credit a
        // top-up fails in the suite rather than only against a real database.
        if (transaction.ProviderPaymentId is not null)
        {
            var providerClash = Transactions.Any(t =>
                !t.IsDeleted &&
                t.WalletId == transaction.WalletId &&
                t.Type == transaction.Type &&
                t.ProviderPaymentId == transaction.ProviderPaymentId);

            if (providerClash)
                throw new InvalidOperationException(
                    $"Unique index violation: a {transaction.Type} transaction already exists for wallet "
                    + $"{transaction.WalletId} and provider payment {transaction.ProviderPaymentId}.");
        }

        // (WalletId, Type, PolicyYearNumber) filtered on Status <> Failed — package-insurance
        // claim-time race guard (issue #104). Set at claim time, unlike ProviderPaymentId, so this
        // is what makes two concurrent claims for the same driver+year a database-level conflict.
        if (transaction.PolicyYearNumber is not null)
        {
            var yearClash = Transactions.Any(t =>
                !t.IsDeleted &&
                t.Status != WalletTransactionStatus.Failed &&
                t.WalletId == transaction.WalletId &&
                t.Type == transaction.Type &&
                t.PolicyYearNumber == transaction.PolicyYearNumber);

            if (yearClash)
                throw new DbUpdateException(
                    $"Unique index violation: a {transaction.Type} transaction already exists for wallet "
                    + $"{transaction.WalletId} and policy year {transaction.PolicyYearNumber}.");
        }
    }

    public Task<bool> HasTransactionForProviderPaymentAsync(Guid walletId, string providerPaymentId, WalletTransactionType type, CancellationToken ct = default)
        => Task.FromResult(Transactions.Any(t =>
            !t.IsDeleted && t.WalletId == walletId && t.ProviderPaymentId == providerPaymentId && t.Type == type));

    public Task<List<WalletTransaction>> GetTransactionsByWalletIdAsync(Guid walletId, DateTime? startDate = null, DateTime? endDate = null, WalletTransactionType? type = null, CancellationToken ct = default)
        => Task.FromResult(Transactions.Where(t => t.WalletId == walletId).ToList());

    /// <summary>Mirrors the real query: cash earnings are excluded via IsCashEarning.</summary>
    public Task SaveClaimedWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default)
    {
        // Legs are already in Transactions from the claim; nothing to add, mirroring the real Update.
        if (!Transactions.Contains(outbound)) Transactions.Add(outbound);
        if (!Transactions.Contains(inbound)) Transactions.Add(inbound);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(DriverWallet Wallet, WalletTransaction Outbound, WalletTransaction Inbound)>>
        GetPendingWalletTransfersAsync(DateTime createdBefore, int limit, CancellationToken ct = default)
    {
        var results = new List<(DriverWallet, WalletTransaction, WalletTransaction)>();
        foreach (var o in Transactions
            .Where(t => !t.IsDeleted && t.Status == WalletTransactionStatus.Pending
                        && t.Type == WalletTransactionType.WalletTransferOut
                        && t.ProviderPaymentId != null && t.CreatedAt < createdBefore)
            .OrderBy(t => t.CreatedAt).Take(limit))
        {
            var inbound = Transactions.FirstOrDefault(t => !t.IsDeleted && t.WalletId == o.WalletId
                && t.Type == WalletTransactionType.WalletTransferIn && t.ProviderPaymentId == o.ProviderPaymentId);
            var wallet = Wallets.FirstOrDefault(w => w.Id == o.WalletId);
            if (inbound is not null && wallet is not null) results.Add((wallet, o, inbound));
        }
        return Task.FromResult<IReadOnlyList<(DriverWallet, WalletTransaction, WalletTransaction)>>(results);
    }

    public Task<bool> HasPendingTransactionsAsync(Guid walletId, CancellationToken ct = default)
        => Task.FromResult(Transactions.Any(t =>
            t.WalletId == walletId && !t.IsDeleted && t.Status == WalletTransactionStatus.Pending));

    public Task<decimal> GetCalculatedPersonalBalanceAsync(Guid walletId, CancellationToken ct = default)
        => Task.FromResult(Transactions
            .Where(t => t.WalletId == walletId && t.Type == WalletTransactionType.Earning && !t.IsCashEarning && !t.IsDeleted)
            .Sum(t => t.Amount));

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    // --- Driver top-ups ---
    // Ordering here mirrors the real SQL exactly, because the ordering *is* the defect under
    // test: the reconciliation query used to take the newest records and starve the oldest.

    public List<DriverTopUp> TopUps { get; } = new();

    public Task<DriverTopUp> CreateTopUpAsync(DriverTopUp topUp, CancellationToken ct = default)
    {
        TopUps.Add(topUp);
        return Task.FromResult(topUp);
    }

    public Task<DriverTopUp?> GetTopUpByIdAsync(Guid topUpId, CancellationToken ct = default)
        => Task.FromResult(TopUps.FirstOrDefault(t => t.Id == topUpId));

    public Task<DriverTopUp?> GetTopUpByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default)
        => Task.FromResult(TopUps.FirstOrDefault(t => t.Provider == provider && t.ProviderPaymentId == providerPaymentId));

    public Task<DriverTopUp?> GetTopUpByExternalIdAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult(TopUps.FirstOrDefault(t => t.ExternalId == externalId));

    public Task<IReadOnlyList<DriverTopUp>> GetTopUpsByDriverIdAsync(Guid driverId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DriverTopUp>>(
            TopUps.Where(t => t.DriverId == driverId).OrderByDescending(t => t.CreatedAt).ToList());

    public Task<IReadOnlyList<WithdrawalRequest>> GetWithdrawalsAwaitingReconciliationAsync(DateTime requestedBefore, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WithdrawalRequest>>(
            WithdrawalRequests.Where(w => w.Status == WithdrawalStatus.Approved
                                          && w.RequestedAt <= requestedBefore
                                          && w.ProviderDisbursementId != null)
                .OrderBy(w => w.RequestedAt)
                .Take(limit)
                .ToList());

    public Task<IReadOnlyList<DriverTopUp>> GetTopUpsAwaitingReconciliationAsync(DateTime createdBefore, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DriverTopUp>>(
            TopUps.Where(t => t.Status == DriverTopUpStatus.Pending
                              && t.CreatedAt <= createdBefore
                              && t.ProviderPaymentId != null)
                  .OrderBy(t => t.CreatedAt)
                  .Take(limit)
                  .ToList());

    public Task<IReadOnlyList<DriverTopUp>> GetUncreditedExpiredTopUpsAsync(DateTime createdSince, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DriverTopUp>>(
            TopUps.Where(t => t.Status == DriverTopUpStatus.Expired
                              && t.CreditedAt == null
                              && t.ProviderPaymentId != null
                              && t.CreatedAt >= createdSince)
                  .OrderBy(t => t.CreatedAt)
                  .Take(limit)
                  .ToList());

    public Task SaveTopUpCreditAsync(DriverTopUp topUp, DriverWallet wallet, WalletTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction != null)
        {
            GuardUnique(transaction);
            Transactions.Add(transaction);
        }

        if (!TopUps.Contains(topUp))
            TopUps.Add(topUp);

        return Task.CompletedTask;
    }

    // --- Withdrawal / top-up / mission surface: not part of the wallet-crediting paths under
    // test. These throw rather than returning defaults so a test that strays into them fails
    // loudly instead of silently asserting against empty data.

    private static T NotUsed<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => throw new NotImplementedException($"{member} is not exercised by the wallet-crediting tests.");

    public Task<List<WithdrawalRequest>> GetWithdrawalRequestsByDriverIdAsync(Guid driverId, WithdrawalStatus? status = null, CancellationToken ct = default) => NotUsed<Task<List<WithdrawalRequest>>>();
    public Task<WithdrawalRequest?> GetWithdrawalByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default)
        => Task.FromResult(WithdrawalRequests.FirstOrDefault(w => w.DriverId == driverId && w.IdempotencyKey == idempotencyKey));
    public Task<WithdrawalRequest?> GetWithdrawalRequestByIdAsync(Guid requestId, CancellationToken ct = default)
        => Task.FromResult(WithWallet(WithdrawalRequests.FirstOrDefault(w => w.Id == requestId)));

    /// <summary>
    /// Populates the Wallet navigation, because the real queries that hand a WithdrawalRequest
    /// to a handler all Include it and the handlers read it directly. Without this the fake
    /// would report "Wallet not found" for code that works perfectly in production.
    /// </summary>
    private WithdrawalRequest? WithWallet(WithdrawalRequest? withdrawal)
    {
        if (withdrawal is null) return null;

        var wallet = Wallets.FirstOrDefault(w => w.Id == withdrawal.WalletId);
        if (wallet is not null)
        {
            typeof(WithdrawalRequest)
                .GetProperty(nameof(WithdrawalRequest.Wallet))!
                .SetValue(withdrawal, wallet);
        }

        return withdrawal;
    }
    public Task<WithdrawalRequest?> GetWithdrawalByProviderDisbursementIdAsync(string provider, string providerDisbursementId, CancellationToken ct = default)
        // WithWallet mirrors the real query's Include: the webhook handler reads withdrawal.Wallet
        // directly, and without it the fake would report "Wallet not found" for code that works.
        => Task.FromResult(WithWallet(WithdrawalRequests.FirstOrDefault(w =>
            w.Provider == provider && w.ProviderDisbursementId == providerDisbursementId)));
    public Task<WalletTransaction?> GetWithdrawalTransactionByRequestIdAsync(Guid withdrawalRequestId, CancellationToken ct = default)
        => Task.FromResult(Transactions.FirstOrDefault(t => t.RelatedWithdrawalRequestId == withdrawalRequestId));
    public Task<WithdrawalRequest> CreateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default)
    {
        // Real behaviour: the row exists from here on, so a replay lookup by idempotency key finds
        // it even if the provider call that follows fails. That ordering is what makes a retried
        // withdrawal return the original request instead of starting a second one.
        WithdrawalRequests.Add(request);
        return Task.FromResult(request);
    }
    public Task<WithdrawalRequest> UpdateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default)
    {
        if (!WithdrawalRequests.Contains(request)) WithdrawalRequests.Add(request);
        return Task.FromResult(request);
    }
    public Task UpdateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default)
    {
        // The row already exists (it was claimed before the provider call); this only persists the
        // status change, so it must not re-run GuardUnique or the claim would collide with itself.
        if (!Transactions.Contains(transaction)) Transactions.Add(transaction);
        return Task.CompletedTask;
    }
    public Task SaveWithdrawalDisbursementResultAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        if (!WithdrawalRequests.Contains(withdrawal)) WithdrawalRequests.Add(withdrawal);
        if (!Transactions.Contains(transaction)) Transactions.Add(transaction);
        return Task.CompletedTask;
    }

    public Task SaveWithdrawalReservationAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        if (FailNextReservationWithConcurrency)
        {
            FailNextReservationWithConcurrency = false;
            // Nothing is persisted, exactly as a real rolled-back SaveChanges would leave it.
            throw new DbUpdateConcurrencyException("Simulated: another withdrawal won the race for this wallet.");
        }

        WithdrawalRequests.Add(withdrawal);
        Transactions.Add(transaction);
        Reservations.Add(withdrawal);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WithdrawalRequest>> GetUnconfirmedReservationsAsync(DateTime requestedBefore, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WithdrawalRequest>>(
            WithdrawalRequests.Where(w => w.Status == WithdrawalStatus.Pending
                                          && w.RequestedAt <= requestedBefore
                                          && w.ProviderDisbursementId == null)
                .OrderBy(w => w.RequestedAt)
                .Take(limit)
                .ToList());
    public Task<DriverTopUp?> GetTopUpByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default) => NotUsed<Task<DriverTopUp?>>();
    public Task<IReadOnlyList<DriverTopUp>> GetTopUpsAsync(DateTime? from = null, DateTime? to = null, DriverTopUpStatus? status = null, CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<DriverTopUp>>>();
    public Task<IReadOnlyList<DriverTopUp>> GetPendingTopUpsToExpireAsync(DateTime utcNow, TimeSpan maxAgeWithoutExpiry, CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<DriverTopUp>>>();
    public Task<IReadOnlyList<DriverWallet>> GetWalletsBelowTopUpThresholdAsync(decimal threshold, CancellationToken ct = default) => NotUsed<Task<IReadOnlyList<DriverWallet>>>();
    public Task<List<DriverMission>> GetMissionsByDriverIdAsync(Guid driverId, MissionStatus? status = null, CancellationToken ct = default) => NotUsed<Task<List<DriverMission>>>();
    public Task<DriverMission?> GetMissionByIdAsync(Guid missionId, CancellationToken ct = default) => NotUsed<Task<DriverMission?>>();
    public Task<DriverMission> CreateMissionAsync(DriverMission mission, CancellationToken ct = default) => NotUsed<Task<DriverMission>>();
}
