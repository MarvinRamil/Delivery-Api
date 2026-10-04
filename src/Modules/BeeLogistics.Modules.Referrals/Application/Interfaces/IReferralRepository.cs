using BeeLogistics.Modules.Referrals.Domain;

namespace BeeLogistics.Modules.Referrals.Application.Interfaces;

public interface IReferralRepository
{
    Task<ReferralCode?> GetReferralCodeByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<ReferralCode?> GetReferralCodeByCodeAsync(string code, CancellationToken ct = default);
    Task<ReferralCode> CreateReferralCodeAsync(ReferralCode referralCode, CancellationToken ct = default);
    Task<Referral?> GetReferralByReferredUserIdAsync(Guid referredUserId, CancellationToken ct = default);
    Task<Referral> CreateReferralAsync(Referral referral, CancellationToken ct = default);
    Task<List<Referral>> GetReferralsByReferrerIdAsync(Guid referrerId, ReferralStatus? status = null, CancellationToken ct = default);
    Task<Referral?> GetReferralByIdAsync(Guid referralId, CancellationToken ct = default);
    Task<UserPoints?> GetUserPointsByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task<UserPoints> CreateUserPointsAsync(UserPoints userPoints, CancellationToken ct = default);
    Task<UserPoints> UpdateUserPointsAsync(UserPoints userPoints, CancellationToken ct = default);
    Task<List<PointsTransaction>> GetPointsTransactionsByUserIdAsync(Guid userId, DateTime? startDate = null, DateTime? endDate = null, CancellationToken ct = default);
    Task<PointsTransaction> CreatePointsTransactionAsync(PointsTransaction transaction, CancellationToken ct = default);
}

