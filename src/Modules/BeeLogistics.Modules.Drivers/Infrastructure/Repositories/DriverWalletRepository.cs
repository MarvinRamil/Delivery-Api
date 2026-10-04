using BeeLogistics.Modules.Drivers.Application.Interfaces;
using BeeLogistics.Modules.Drivers.Domain;
using BeeLogistics.Modules.Payment.Application.Gateways;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Drivers.Infrastructure.Repositories;

public class DriverWalletRepository : IDriverWalletRepository
{
    private readonly DriversDbContext _context;

    public DriverWalletRepository(DriversDbContext context)
    {
        _context = context;
    }

    public async Task<DriverWallet?> GetWalletByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.DriverWallets
            .Include(w => w.Transactions.OrderByDescending(t => t.TransactionDate).Take(10))
            .FirstOrDefaultAsync(w => w.DriverId == driverId, ct);
    }

    public async Task<IReadOnlyList<DriverWallet>> GetActivatedPayMongoWalletsAsync(Guid? afterId, int limit, CancellationToken ct = default)
    {
        var query = _context.DriverWallets
            .Where(w => w.PayMongoActivationStatus == PayMongoActivationStatus.Activated);

        if (afterId.HasValue)
            query = query.Where(w => w.Id > afterId.Value);

        return await query.OrderBy(w => w.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<DriverWallet?> GetWalletByPayMongoAccountIdAsync(string payMongoAccountId, CancellationToken ct = default)
    {
        return await _context.DriverWallets
            .FirstOrDefaultAsync(w => w.PayMongoAccountId == payMongoAccountId, ct);
    }

    public async Task<DriverWallet?> GetWalletByPayMongoAccountNumberAsync(string accountNumber, CancellationToken ct = default)
    {
        return await _context.DriverWallets
            .FirstOrDefaultAsync(w => w.PayMongoAccountNumber == accountNumber, ct);
    }

    public async Task<DriverWallet> CreateWalletAsync(DriverWallet wallet, CancellationToken ct = default)
    {
        await _context.DriverWallets.AddAsync(wallet, ct);
        await _context.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task<DriverWallet> UpdateWalletAsync(DriverWallet wallet, CancellationToken ct = default)
    {
        _context.DriverWallets.Update(wallet);
        await _context.SaveChangesAsync(ct);
        return wallet;
    }

    public async Task<List<WalletTransaction>> GetTransactionsByWalletIdAsync(Guid walletId, DateTime? startDate = null, DateTime? endDate = null, WalletTransactionType? type = null, CancellationToken ct = default)
    {
        var query = _context.WalletTransactions
            .Where(t => t.WalletId == walletId);

        if (startDate.HasValue)
        {
            query = query.Where(t => t.TransactionDate >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(t => t.TransactionDate <= endDate.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(t => t.Type == type.Value);
        }

        return await query.OrderByDescending(t => t.TransactionDate).ToListAsync(ct);
    }

    public async Task<WalletTransaction> CreateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default)
    {
        await _context.WalletTransactions.AddAsync(transaction, ct);
        await _context.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<bool> HasTransactionForProviderPaymentAsync(Guid walletId, string providerPaymentId, WalletTransactionType type, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .AnyAsync(t => t.WalletId == walletId && t.ProviderPaymentId == providerPaymentId && t.Type == type, ct);
    }

    public async Task<bool> HasTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .AnyAsync(t => t.WalletId == walletId && t.RelatedBookingId == bookingId && t.Type == type, ct);
    }

    public async Task<WalletTransaction?> GetTransactionForBookingAsync(Guid walletId, Guid bookingId, WalletTransactionType type, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .FirstOrDefaultAsync(t => t.WalletId == walletId && t.RelatedBookingId == bookingId && t.Type == type, ct);
    }

    public async Task ApplyTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        // One SaveChanges for both writes. Previously the transaction row and the balance were
        // committed separately, so a failure in between (including a legitimate xmin concurrency
        // conflict, which is exactly what retries exist for) left a transaction row with no
        // balance change - and the retry then saw the row, assumed the work was done, and
        // skipped. Committing them together removes that window entirely.
        await _context.WalletTransactions.AddAsync(transaction, ct);
        _context.DriverWallets.Update(wallet);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveClaimedTransactionAsync(DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        // Update, not Add: this row already exists. Inserting it again would violate the unique
        // index it was written to claim in the first place.
        _context.WalletTransactions.Update(transaction);
        _context.DriverWallets.Update(wallet);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default)
    {
        // Same reasoning as ApplyTransactionAsync above, doubled: a transfer moves money between
        // two buckets of the same wallet, so the debit leg, the credit leg and the new balances
        // are one indivisible fact. Committing them separately - as this path used to, in three
        // round trips - could leave balances that no sequence of ledger rows explains.
        await _context.WalletTransactions.AddAsync(outbound, ct);
        await _context.WalletTransactions.AddAsync(inbound, ct);
        _context.DriverWallets.Update(wallet);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveClaimedWalletTransferAsync(DriverWallet wallet, WalletTransaction outbound, WalletTransaction inbound, CancellationToken ct = default)
    {
        // Update, not Add: both legs were inserted before the provider call, so re-inserting would
        // duplicate them. Same distinction as SaveClaimedTransactionAsync against ApplyTransactionAsync.
        _context.WalletTransactions.Update(outbound);
        _context.WalletTransactions.Update(inbound);
        _context.DriverWallets.Update(wallet);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(DriverWallet Wallet, WalletTransaction Outbound, WalletTransaction Inbound)>>
        GetPendingWalletTransfersAsync(DateTime createdBefore, int limit, CancellationToken ct = default)
    {
        var outbound = await _context.WalletTransactions
            .Where(t => !t.IsDeleted
                        && t.Status == WalletTransactionStatus.Pending
                        && t.Type == WalletTransactionType.WalletTransferOut
                        && t.ProviderPaymentId != null
                        && t.CreatedAt < createdBefore)
            .OrderBy(t => t.CreatedAt)   // oldest first: newest-first starves the genuinely stuck
            .Take(limit)
            .ToListAsync(ct);

        var results = new List<(DriverWallet, WalletTransaction, WalletTransaction)>(outbound.Count);
        foreach (var o in outbound)
        {
            var inbound = await _context.WalletTransactions.FirstOrDefaultAsync(
                t => !t.IsDeleted
                     && t.WalletId == o.WalletId
                     && t.Type == WalletTransactionType.WalletTransferIn
                     && t.ProviderPaymentId == o.ProviderPaymentId, ct);
            if (inbound is null) continue;   // half a pair cannot be settled safely

            var wallet = await _context.DriverWallets.FirstOrDefaultAsync(w => w.Id == o.WalletId, ct);
            if (wallet is null) continue;

            results.Add((wallet, o, inbound));
        }

        return results;
    }

    public async Task<bool> HasPendingTransactionsAsync(Guid walletId, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .AnyAsync(t => t.WalletId == walletId
                           && !t.IsDeleted
                           && t.Status == WalletTransactionStatus.Pending, ct);
    }

    public async Task<IReadOnlyList<WalletTransaction>> GetUnpaidCommissionsAsync(
        Guid walletId, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            // Oldest first: the debt a driver has carried longest is the one to clear first.
            .OrderBy(t => t.TransactionDate)
            .ToListAsync(ct);
    }

    public async Task<WalletTransaction?> GetPendingCashBondPaymentAsync(Guid walletId, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.CashBondPayment
                        && t.Status == WalletTransactionStatus.Pending)
            // Oldest first: if several already exist, the first one is the attempt to resolve.
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<(DriverWallet Wallet, WalletTransaction Transaction)?> GetCashBondPaymentWithWalletAsync(
        Guid transactionId, CancellationToken ct = default)
    {
        var transaction = await _context.WalletTransactions.FirstOrDefaultAsync(
            t => t.Id == transactionId
                 && !t.IsDeleted
                 && t.Type == WalletTransactionType.CashBondPayment, ct);
        if (transaction is null) return null;

        var wallet = await _context.DriverWallets.FirstOrDefaultAsync(w => w.Id == transaction.WalletId, ct);
        return wallet is null ? null : (wallet, transaction);
    }

    // ── Package insurance (issue #104) ──────────────────────────────────────────────────────

    public async Task<DriverPackageInsurancePolicy?> GetInsurancePolicyByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.DriverPackageInsurancePolicies
            .FirstOrDefaultAsync(p => p.DriverId == driverId, ct);
    }

    public async Task<DriverPackageInsurancePolicy> CreateInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default)
    {
        await _context.DriverPackageInsurancePolicies.AddAsync(policy, ct);
        await _context.SaveChangesAsync(ct);
        return policy;
    }

    public async Task<WalletTransaction?> GetPendingInsurancePremiumPaymentAsync(Guid walletId, int policyYearNumber, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .Where(t => t.WalletId == walletId
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PackageInsurancePayment
                        && t.PolicyYearNumber == policyYearNumber
                        && t.Status == WalletTransactionStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<(DriverWallet Wallet, DriverPackageInsurancePolicy Policy, WalletTransaction Transaction)?> GetInsurancePremiumPaymentWithPolicyAsync(
        Guid transactionId, CancellationToken ct = default)
    {
        var transaction = await _context.WalletTransactions.FirstOrDefaultAsync(
            t => t.Id == transactionId
                 && !t.IsDeleted
                 && t.Type == WalletTransactionType.PackageInsurancePayment, ct);
        if (transaction is null) return null;

        var wallet = await _context.DriverWallets.FirstOrDefaultAsync(w => w.Id == transaction.WalletId, ct);
        if (wallet is null) return null;

        var policy = await _context.DriverPackageInsurancePolicies.FirstOrDefaultAsync(p => p.DriverId == wallet.DriverId, ct);
        return policy is null ? null : (wallet, policy, transaction);
    }

    public async Task SaveClaimedInsurancePremiumAsync(DriverPackageInsurancePolicy policy, WalletTransaction transaction, CancellationToken ct = default)
    {
        // One SaveChanges: the policy's paid-through year and the transaction's Completed status
        // are one indivisible fact, and this is where the policy's xmin token is checked - a
        // second concurrent settlement of the same year fails here, before either write lands.
        _context.DriverPackageInsurancePolicies.Update(policy);
        _context.WalletTransactions.Update(transaction);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DriverPackageInsurancePolicy>> GetActiveInsurancePoliciesAsync(Guid? afterId, int limit, CancellationToken ct = default)
    {
        var query = _context.DriverPackageInsurancePolicies
            .Where(p => p.Status == DriverPackageInsurancePolicyStatus.Active);

        if (afterId.HasValue)
            query = query.Where(p => p.Id > afterId.Value);

        return await query.OrderBy(p => p.Id).Take(limit).ToListAsync(ct);
    }

    public async Task SaveInsurancePolicyAsync(DriverPackageInsurancePolicy policy, CancellationToken ct = default)
    {
        _context.DriverPackageInsurancePolicies.Update(policy);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> HasUnpaidCommissionAsync(Guid walletId, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .AnyAsync(t => t.WalletId == walletId
                           && !t.IsDeleted
                           && t.Type == WalletTransactionType.PlatformCommission
                           && t.Status == WalletTransactionStatus.Pending, ct);
    }

    public async Task<IReadOnlySet<Guid>> GetWalletIdsWithUnpaidCommissionAsync(
        IReadOnlyCollection<Guid> walletIds, CancellationToken ct = default)
    {
        if (walletIds.Count == 0)
            return new HashSet<Guid>();

        var owing = await _context.WalletTransactions
            .Where(t => walletIds.Contains(t.WalletId)
                        && !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            .Select(t => t.WalletId)
            .Distinct()
            .ToListAsync(ct);

        return owing.ToHashSet();
    }

    public async Task<IReadOnlyList<Guid>> GetDriversEligibleForCashJobAsync(
        IReadOnlyCollection<Guid> driverIds, decimal requiredCommission, CancellationToken ct = default)
    {
        if (driverIds.Count == 0)
            return Array.Empty<Guid>();

        // Composed as one query rather than fetching wallets and filtering in memory: on a busy
        // area the candidate set is large, and this runs before every booking goes out.
        var owing = _context.WalletTransactions
            .Where(t => !t.IsDeleted
                        && t.Type == WalletTransactionType.PlatformCommission
                        && t.Status == WalletTransactionStatus.Pending)
            .Select(t => t.WalletId);

        return await _context.DriverWallets
            .Where(w => driverIds.Contains(w.DriverId)
                        // Their money has to be somewhere we can sweep from. A driver who has not
                        // finished wallet setup has no child wallet, so the fee could never be
                        // collected from them.
                        && w.PayMongoAccountId != null
                        && w.PayMongoAccountNumber != null
                        && w.PayMongoActivationStatus == PayMongoActivationStatus.Activated
                        && w.Balance >= requiredCommission
                        && !owing.Contains(w.Id)
                        // Cashbond gate (issue #103): a driver who has not paid the upfront
                        // deposit for their vehicle type cannot be offered bookings. This is the
                        // one choke point every dispatch path already funnels through.
                        && w.CashBondBalance > 0)
            .Select(w => w.DriverId)
            .ToListAsync(ct);
    }

    public async Task<decimal> GetCalculatedPersonalBalanceAsync(Guid walletId, CancellationToken ct = default)
    {
        // 1. Sum additions (Earnings, Transfers In, Refunds) - only Completed
        // Cash earnings are excluded: they represent physical cash already collected by the
        // driver and do not increase the withdrawable digital balance. Identified by the
        // IsCashEarning column rather than by sniffing "(cash)" out of the description, which
        // silently reclassified money whenever the description wording changed.
        var additions = await _context.WalletTransactions
            .Where(t => t.WalletId == walletId &&
                        t.Bucket == WalletBucket.Personal &&
                        t.Status == WalletTransactionStatus.Completed &&
                        (
                            (t.Type == WalletTransactionType.Earning && !t.IsCashEarning) ||
                            t.Type == WalletTransactionType.WalletTransferIn ||
                            // BeeWallet QR top-ups land here. Every other TopUp row lives in the
                            // TopUp bucket, so the bucket filter above already keeps Cash Wallet
                            // credits out of this sum.
                            t.Type == WalletTransactionType.TopUp ||
                            t.Type == WalletTransactionType.Refund ||
                            t.Type == WalletTransactionType.Payout || // In case Payout is used as a credit
                            t.Type == WalletTransactionType.CashDeficitAdjustment // Adjustments might be credits
                        ))
            .SumAsync(t => t.Amount, ct);

        // 2. Sum deductions (Withdrawals, Transfers Out)
        // For Withdrawals: Include Pending and Completed. Exclude Failed.
        // For Transfers Out: Only Completed.
        var withdrawals = await _context.WalletTransactions
            .Where(t => t.WalletId == walletId &&
                        t.Bucket == WalletBucket.Personal &&
                        t.Type == WalletTransactionType.Withdrawal &&
                        t.Status != WalletTransactionStatus.Failed)
            .SumAsync(t => t.Amount, ct);

        var transfersOut = await _context.WalletTransactions
            .Where(t => t.WalletId == walletId &&
                        t.Bucket == WalletBucket.Personal &&
                        t.Type == WalletTransactionType.WalletTransferOut &&
                        t.Status == WalletTransactionStatus.Completed)
            .SumAsync(t => t.Amount, ct);

        var earningReversals = await _context.WalletTransactions
            .Where(t => t.WalletId == walletId &&
                        t.Bucket == WalletBucket.Personal &&
                        t.Type == WalletTransactionType.EarningReversal &&
                        t.Status == WalletTransactionStatus.Completed)
            .SumAsync(t => t.Amount, ct);

        return additions - withdrawals - transfersOut - earningReversals;
    }

    public async Task<List<WithdrawalRequest>> GetWithdrawalRequestsByDriverIdAsync(Guid driverId, WithdrawalStatus? status = null, CancellationToken ct = default)
    {
        var query = _context.WithdrawalRequests
            .Where(wr => wr.DriverId == driverId);

        if (status.HasValue)
        {
            query = query.Where(wr => wr.Status == status.Value);
        }

        return await query.OrderByDescending(wr => wr.RequestedAt).ToListAsync(ct);
    }

    public async Task<WithdrawalRequest?> GetWithdrawalByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = idempotencyKey.Trim();
        return await _context.WithdrawalRequests
            .FirstOrDefaultAsync(wr => wr.DriverId == driverId && wr.IdempotencyKey == key, ct);
    }

    public async Task<WithdrawalRequest?> GetWithdrawalRequestByIdAsync(Guid requestId, CancellationToken ct = default)
    {
        return await _context.WithdrawalRequests
            .Include(wr => wr.Wallet)
            .FirstOrDefaultAsync(wr => wr.Id == requestId, ct);
    }

    public async Task<WithdrawalRequest?> GetWithdrawalByProviderDisbursementIdAsync(string provider, string providerDisbursementId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerDisbursementId))
            return null;
        var trimmed = providerDisbursementId.Trim();
        return await _context.WithdrawalRequests
            .Include(wr => wr.Wallet)
            .FirstOrDefaultAsync(wr => wr.Provider == provider && wr.ProviderDisbursementId == trimmed, ct);
    }

    public async Task<WalletTransaction?> GetWithdrawalTransactionByRequestIdAsync(Guid withdrawalRequestId, CancellationToken ct = default)
    {
        return await _context.WalletTransactions
            .FirstOrDefaultAsync(t => t.RelatedWithdrawalRequestId == withdrawalRequestId && t.Type == WalletTransactionType.Withdrawal, ct);
    }

    public async Task<WithdrawalRequest> CreateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default)
    {
        await _context.WithdrawalRequests.AddAsync(request, ct);
        await _context.SaveChangesAsync(ct);
        return request;
    }

    public async Task<WithdrawalRequest> UpdateWithdrawalRequestAsync(WithdrawalRequest request, CancellationToken ct = default)
    {
        _context.WithdrawalRequests.Update(request);
        await _context.SaveChangesAsync(ct);
        return request;
    }

    public async Task UpdateTransactionAsync(WalletTransaction transaction, CancellationToken ct = default)
    {
        _context.WalletTransactions.Update(transaction);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveWithdrawalReservationAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        // One SaveChanges, so the wallet debit, the withdrawal row and the ledger row are a
        // single transaction. This is also where the wallet's xmin token is checked: a second
        // concurrent withdrawal for the same driver fails HERE, before any money has moved.
        await _context.WithdrawalRequests.AddAsync(withdrawal, ct);
        await _context.WalletTransactions.AddAsync(transaction, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveWithdrawalDisbursementResultAsync(WithdrawalRequest withdrawal, DriverWallet wallet, WalletTransaction transaction, CancellationToken ct = default)
    {
        _context.WithdrawalRequests.Update(withdrawal);
        _context.DriverWallets.Update(wallet);
        _context.WalletTransactions.Update(transaction);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveTopUpCreditAsync(DriverTopUp topUp, DriverWallet wallet, WalletTransaction? transaction = null, CancellationToken ct = default)
    {
        _context.DriverTopUps.Update(topUp);
        _context.DriverWallets.Update(wallet);
        if (transaction != null)
            await _context.WalletTransactions.AddAsync(transaction, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<DriverTopUp> CreateTopUpAsync(DriverTopUp topUp, CancellationToken ct = default)
    {
        await _context.DriverTopUps.AddAsync(topUp, ct);
        await _context.SaveChangesAsync(ct);
        return topUp;
    }

    public async Task<DriverTopUp?> GetTopUpByIdAsync(Guid topUpId, CancellationToken ct = default)
    {
        return await _context.DriverTopUps.FirstOrDefaultAsync(t => t.Id == topUpId, ct);
    }

    public async Task<DriverTopUp?> GetTopUpByDriverAndIdempotencyKeyAsync(Guid driverId, string idempotencyKey, CancellationToken ct = default)
    {
        return await _context.DriverTopUps.FirstOrDefaultAsync(
            t => t.DriverId == driverId && t.IdempotencyKey == idempotencyKey,
            ct);
    }

    public async Task<DriverTopUp?> GetTopUpByProviderPaymentIdAsync(string provider, string providerPaymentId, CancellationToken ct = default)
    {
        return await _context.DriverTopUps.FirstOrDefaultAsync(t => t.Provider == provider && t.ProviderPaymentId == providerPaymentId, ct);
    }

    public async Task<DriverTopUp?> GetTopUpByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        return await _context.DriverTopUps.FirstOrDefaultAsync(t => t.ExternalId == externalId, ct);
    }

    public async Task<IReadOnlyList<DriverTopUp>> GetTopUpsByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.DriverTopUps
            .Where(t => t.DriverId == driverId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DriverTopUp>> GetTopUpsAsync(DateTime? from = null, DateTime? to = null, DriverTopUpStatus? status = null, CancellationToken ct = default)
    {
        var query = _context.DriverTopUps.AsQueryable();

        if (from.HasValue)
            query = query.Where(t => t.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(t => t.CreatedAt <= to.Value);
        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        return await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WithdrawalRequest>> GetUnconfirmedReservationsAsync(DateTime requestedBefore, int limit, CancellationToken ct = default)
    {
        // Reservations whose provider call never came back with an id: the funds are already
        // debited but we do not know whether the transfer was created. Invisible to the
        // Approved-only query below, so without this they would stay debited forever.
        return await _context.WithdrawalRequests
            .Where(w => w.Status == WithdrawalStatus.Pending
                        && w.RequestedAt <= requestedBefore
                        && w.ProviderDisbursementId == null)
            .OrderBy(w => w.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WithdrawalRequest>> GetWithdrawalsAwaitingReconciliationAsync(DateTime requestedBefore, int limit, CancellationToken ct = default)
    {
        // Approved is the state a withdrawal sits in between "provider accepted it" and
        // "the rail settled it". Without a transfer.* webhook nothing ever moves it on, so
        // the driver's PendingPayout is held indefinitely — this query is what finds those.
        return await _context.WithdrawalRequests
            .Where(w => w.Status == WithdrawalStatus.Approved
                        && w.RequestedAt <= requestedBefore
                        && w.ProviderDisbursementId != null)
            .OrderBy(w => w.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DriverTopUp>> GetTopUpsAwaitingReconciliationAsync(DateTime createdBefore, int limit, CancellationToken ct = default)
    {
        // OrderBy, not OrderByDescending: see the interface docs. Newest-first starves the
        // oldest records permanently once the pending pool outgrows the per-run cap.
        return await _context.DriverTopUps
            .Where(t => t.Status == DriverTopUpStatus.Pending
                        && t.CreatedAt <= createdBefore
                        && t.ProviderPaymentId != null)
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DriverTopUp>> GetUncreditedExpiredTopUpsAsync(DateTime createdSince, int limit, CancellationToken ct = default)
    {
        // Windowed on CreatedAt rather than on when the record was closed: UpdatedAt moves for
        // any write, so it cannot be trusted to mean "expired at". Creation time is stable, and
        // expiry happens a fixed interval after it.
        return await _context.DriverTopUps
            .Where(t => t.Status == DriverTopUpStatus.Expired
                        && t.CreditedAt == null
                        && t.ProviderPaymentId != null
                        && t.CreatedAt >= createdSince)
            .OrderBy(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DriverTopUp>> GetPendingTopUpsToExpireAsync(DateTime utcNow, TimeSpan maxAgeWithoutExpiry, CancellationToken ct = default)
    {
        var createdBefore = utcNow - maxAgeWithoutExpiry;
        return await _context.DriverTopUps
            .Where(t => t.Status == DriverTopUpStatus.Pending &&
                (t.ExpiresAt != null && t.ExpiresAt < utcNow || t.ExpiresAt == null && t.CreatedAt < createdBefore))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DriverWallet>> GetWalletsBelowTopUpThresholdAsync(decimal threshold, CancellationToken ct = default)
    {
        return await _context.DriverWallets
            .Where(w => w.TopUpBalance < threshold)
            .OrderBy(w => w.TopUpBalance)
            .ToListAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }

    public async Task<List<DriverMission>> GetMissionsByDriverIdAsync(Guid driverId, MissionStatus? status = null, CancellationToken ct = default)
    {
        var query = _context.DriverMissions
            .Where(m => m.DriverId == driverId);

        if (status.HasValue)
        {
            query = query.Where(m => m.Status == status.Value);
        }

        return await query.OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
    }

    public async Task<DriverMission?> GetMissionByIdAsync(Guid missionId, CancellationToken ct = default)
    {
        return await _context.DriverMissions
            .FirstOrDefaultAsync(m => m.Id == missionId, ct);
    }

    public async Task<DriverMission> CreateMissionAsync(DriverMission mission, CancellationToken ct = default)
    {
        await _context.DriverMissions.AddAsync(mission, ct);
        await _context.SaveChangesAsync(ct);
        return mission;
    }
}

