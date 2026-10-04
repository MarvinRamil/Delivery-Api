using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Domain;
using BeeLogistics.Modules.Verification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Verification.Application.Services;

public class DriverVerificationLookup : IDriverVerificationLookup
{
    private readonly VerificationDbContext _db;

    public DriverVerificationLookup(VerificationDbContext db)
    {
        _db = db;
    }

    public async Task<DriverVerificationSummary?> GetLatestForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await _db.DriverVerifications
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return latest == null ? null : ToSummary(latest);
    }

    public async Task<IReadOnlyDictionary<string, DriverVerificationSummary>> GetLatestForUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<string, DriverVerificationSummary>();

        var rows = await _db.DriverVerifications
            .Where(v => userIds.Contains(v.UserId))
            .GroupBy(v => v.UserId)
            .Select(g => g.OrderByDescending(v => v.CreatedAt).First())
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(v => v.UserId, ToSummary);
    }

    private static DriverVerificationSummary ToSummary(DriverVerification v) =>
        new(v.Status.ToString(), v.FaceMatchScore, v.LivenessScore, v.CompletedAt);
}
