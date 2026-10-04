using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Domain;
using BeeLogistics.Modules.Verification.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Verification.Application.Services;

public class DriverIdDocumentProvider : IDriverIdDocumentProvider
{
    private readonly VerificationDbContext _db;
    private readonly IDiditApiClient _didit;
    private readonly ILogger<DriverIdDocumentProvider> _logger;

    public DriverIdDocumentProvider(VerificationDbContext db, IDiditApiClient didit, ILogger<DriverIdDocumentProvider> logger)
    {
        _db = db;
        _didit = didit;
        _logger = logger;
    }

    private static readonly DriverVerificationStatus[] EligibleStatuses =
        [DriverVerificationStatus.Approved, DriverVerificationStatus.InReview];

    public async Task<DriverIdDocumentImage?> GetLatestAvailableIdImageAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await _db.DriverVerifications
            .Where(v => v.UserId == userId && EligibleStatuses.Contains(v.Status))
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest?.DiditSessionId == null)
        {
            _logger.LogWarning("[DriverIdDocument] No Approved/InReview Didit session on record for user {UserId}", userId);
            return null;
        }

        using var decision = await _didit.GetDecisionAsync(latest.DiditSessionId, cancellationToken);
        if (decision == null)
        {
            _logger.LogWarning("[DriverIdDocument] Decision fetch returned nothing for session {SessionId}", latest.DiditSessionId);
            return null;
        }

        var parsed = DiditDecisionParser.Parse(decision.RootElement);
        if (parsed.FrontImageUrl == null)
        {
            _logger.LogWarning("[DriverIdDocument] Decision for session {SessionId} has no ID front image", latest.DiditSessionId);
            return null;
        }

        var stream = await _didit.DownloadMediaAsync(parsed.FrontImageUrl, cancellationToken);
        if (stream == null)
        {
            _logger.LogWarning("[DriverIdDocument] Failed to download ID front image for session {SessionId}", latest.DiditSessionId);
            return null;
        }

        return new DriverIdDocumentImage(stream, "image/jpeg");
    }
}
