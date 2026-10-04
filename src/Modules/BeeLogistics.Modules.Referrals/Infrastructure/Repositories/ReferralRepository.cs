using BeeLogistics.Modules.Referrals.Application.Interfaces;
using BeeLogistics.Modules.Referrals.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Referrals.Infrastructure.Repositories;

public class ReferralRepository : IReferralRepository
{
    private readonly ReferralsDbContext _context;

    public ReferralRepository(ReferralsDbContext context)
    {
        _context = context;
    }

    public async Task<ReferralCode?> GetReferralCodeByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        // One code per user, active or not — deactivated codes must not spawn duplicates.
        return await _context.ReferralCodes
            .Include(r => r.Referrals)
            .FirstOrDefaultAsync(r => r.UserId == userId, ct);
    }

    public async Task<ReferralCode?> GetReferralCodeByCodeAsync(string code, CancellationToken ct = default)
    {
        return await _context.ReferralCodes
            .Include(r => r.Referrals)
            .FirstOrDefaultAsync(r => r.Code == code && r.IsActive, ct);
    }

    public async Task<ReferralCode> CreateReferralCodeAsync(ReferralCode referralCode, CancellationToken ct = default)
    {
        await _context.ReferralCodes.AddAsync(referralCode, ct);
        await _context.SaveChangesAsync(ct);
        return referralCode;
    }

    public async Task<Referral?> GetReferralByReferredUserIdAsync(Guid referredUserId, CancellationToken ct = default)
    {
        return await _context.Referrals
            .Include(r => r.ReferralCode)
            .FirstOrDefaultAsync(r => r.ReferredUserId == referredUserId, ct);
    }

    public async Task<Referral> CreateReferralAsync(Referral referral, CancellationToken ct = default)
    {
        await _context.Referrals.AddAsync(referral, ct);
        await _context.SaveChangesAsync(ct);
        return referral;
    }

    public async Task<List<Referral>> GetReferralsByReferrerIdAsync(Guid referrerId, ReferralStatus? status = null, CancellationToken ct = default)
    {
        var query = _context.Referrals
            .Include(r => r.ReferralCode)
            .Where(r => r.ReferrerId == referrerId);

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        return await query.OrderByDescending(r => r.ReferredAt).ToListAsync(ct);
    }

    public async Task<Referral?> GetReferralByIdAsync(Guid referralId, CancellationToken ct = default)
    {
        return await _context.Referrals
            .Include(r => r.ReferralCode)
            .FirstOrDefaultAsync(r => r.Id == referralId, ct);
    }

    public async Task<UserPoints?> GetUserPointsByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.UserPoints
            .FirstOrDefaultAsync(up => up.UserId == userId, ct);
    }

    public async Task<UserPoints> CreateUserPointsAsync(UserPoints userPoints, CancellationToken ct = default)
    {
        await _context.UserPoints.AddAsync(userPoints, ct);
        await _context.SaveChangesAsync(ct);
        return userPoints;
    }

    public async Task<UserPoints> UpdateUserPointsAsync(UserPoints userPoints, CancellationToken ct = default)
    {
        _context.UserPoints.Update(userPoints);
        await _context.SaveChangesAsync(ct);
        return userPoints;
    }

    public async Task<List<PointsTransaction>> GetPointsTransactionsByUserIdAsync(Guid userId, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default)
    {
        var query = _context.PointsTransactions
            .Where(pt => pt.UserId == userId);

        if (startDate.HasValue)
        {
            query = query.Where(pt => pt.TransactionDate >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(pt => pt.TransactionDate <= endDate.Value);
        }

        return await query.OrderByDescending(pt => pt.TransactionDate).ToListAsync(ct);
    }

    public async Task<PointsTransaction> CreatePointsTransactionAsync(PointsTransaction transaction, CancellationToken ct = default)
    {
        await _context.PointsTransactions.AddAsync(transaction, ct);
        await _context.SaveChangesAsync(ct);
        return transaction;
    }
}

