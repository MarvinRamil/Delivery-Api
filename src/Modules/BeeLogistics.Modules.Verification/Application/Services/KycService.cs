using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Domain;
using BeeLogistics.Modules.Verification.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Verification.Application.Services;

public class KycService : IKycService
{
    public const string VerificationsBucket = "driver-verifications";

    private readonly VerificationDbContext _db;
    private readonly IDiditApiClient _didit;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<KycService> _logger;
    private readonly IOnLivenessVerified? _onVerified;
    private readonly IPublishEndpoint? _publishEndpoint;
    private readonly IFaceMatchApiClient? _faceMatch;

    public KycService(
        VerificationDbContext db,
        IDiditApiClient didit,
        IFileStorageService fileStorage,
        ILogger<KycService> logger,
        IOnLivenessVerified? onVerified = null,
        IPublishEndpoint? publishEndpoint = null,
        IFaceMatchApiClient? faceMatch = null)
    {
        _db = db;
        _didit = didit;
        _fileStorage = fileStorage;
        _logger = logger;
        _onVerified = onVerified;
        _publishEndpoint = publishEndpoint;
        _faceMatch = faceMatch;
    }

    public async Task<CreateKycSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(userId, cancellationToken);
        if (latest?.Status is DriverVerificationStatus.Approved or DriverVerificationStatus.Legacy)
            throw new InvalidOperationException("Driver identity is already verified.");

        var created = await _didit.CreateSessionAsync(userId, cancellationToken: cancellationToken);

        // Didit hands back the *same* session_id when a driver re-engages a session that never
        // reached a terminal status, so the insert must be idempotent against the unique index
        // on DiditSessionId. Reuse leaves the row untouched: a webhook or poll reconcile may
        // already have advanced it past Pending.
        var existing = await _db.DriverVerifications
            .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);

        if (existing != null)
        {
            _logger.LogInformation("[KYC] Reusing open Didit session {SessionId} for user {UserId} (status {Status})",
                created.SessionId, userId, existing.Status);
            return new CreateKycSessionResult(created.SessionId, created.Url);
        }

        _db.DriverVerifications.Add(new DriverVerification
        {
            UserId = userId,
            DiditSessionId = created.SessionId,
            Status = DriverVerificationStatus.Pending
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[KYC] Didit session {SessionId} created for user {UserId}", created.SessionId, userId);
        }
        catch (DbUpdateException)
        {
            // A concurrent request for the same reused session won the insert. Drop our pending
            // Added entity (it would be retried on any later SaveChanges) and confirm the row.
            _db.ChangeTracker.Clear();
            var raced = await _db.DriverVerifications
                .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);
            if (raced == null)
                throw;

            _logger.LogInformation("[KYC] Concurrent insert for session {SessionId}; reusing", created.SessionId);
        }

        return new CreateKycSessionResult(created.SessionId, created.Url);
    }

    public async Task<KycStatusResult> GetStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(userId, cancellationToken);
        if (latest == null)
            return new KycStatusResult(KycStatusResult.NotStarted, null, null, null, null);

        // Poll fallback: webhook may be delayed/lost — reconcile non-terminal sessions against Didit.
        if (latest.DiditSessionId != null &&
            latest.Status is DriverVerificationStatus.Pending or DriverVerificationStatus.InProgress or DriverVerificationStatus.InReview)
        {
            try
            {
                using var decision = await _didit.GetDecisionAsync(latest.DiditSessionId, cancellationToken);
                if (decision != null)
                {
                    await ApplyDecisionAsync(latest, decision.RootElement, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KYC] Poll reconcile failed for session {SessionId}", latest.DiditSessionId);
            }
        }

        return new KycStatusResult(latest.Status.ToString(), latest.FaceMatchScore, latest.LivenessScore, latest.StatusReason, latest.CompletedAt);
    }

    public async Task ProcessWebhookAsync(string sessionId, string? vendorData, string? diditStatus, CancellationToken cancellationToken = default)
    {
        var record = await _db.DriverVerifications.FirstOrDefaultAsync(v => v.DiditSessionId == sessionId, cancellationToken);
        if (record == null)
        {
            // Session unknown locally (e.g. row lost after create). vendor_data carries our userId.
            if (string.IsNullOrWhiteSpace(vendorData))
            {
                _logger.LogWarning("[KYC] Webhook for unknown session {SessionId} without vendor_data — ignored", sessionId);
                return;
            }
            record = new DriverVerification { UserId = vendorData, DiditSessionId = sessionId };
            _db.DriverVerifications.Add(record);
        }

        var mapped = DiditDecisionParser.MapStatus(diditStatus);

        // Idempotency: webhooks are retried; skip if nothing would change.
        if (record.Status == mapped && (mapped != DriverVerificationStatus.Approved || record.ReferenceSelfiePath != null))
        {
            _logger.LogDebug("[KYC] Webhook for session {SessionId} is a no-op (status {Status})", sessionId, mapped);
            return;
        }

        if (mapped is DriverVerificationStatus.Approved or DriverVerificationStatus.Declined or DriverVerificationStatus.InReview)
        {
            // Re-fetch the decision from the API: media URLs in webhook payloads are short-lived,
            // and this also protects against acting on a forged/partial payload.
            using var decision = await _didit.GetDecisionAsync(sessionId, cancellationToken);
            if (decision != null)
            {
                await ApplyDecisionAsync(record, decision.RootElement, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[KYC] Decision fetch returned nothing for session {SessionId}; applying webhook status only", sessionId);
                await ApplyStatusAsync(record, mapped, null, cancellationToken);
            }
        }
        else
        {
            await ApplyStatusAsync(record, mapped, null, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyDecisionAsync(DriverVerification record, JsonElement decisionRoot, CancellationToken cancellationToken)
    {
        var decision = DiditDecisionParser.Parse(decisionRoot);

        record.FaceMatchScore = decision.FaceMatchScore ?? record.FaceMatchScore;
        record.LivenessScore = decision.LivenessScore ?? record.LivenessScore;
        record.IdDocumentType = decision.IdDocumentType ?? record.IdDocumentType;
        record.IdNumber = decision.IdNumber ?? record.IdNumber;
        record.FullName = decision.FullName ?? record.FullName;
        record.DateOfBirth = decision.DateOfBirth ?? record.DateOfBirth;

        if (decision.Status == DriverVerificationStatus.Approved && record.ReferenceSelfiePath == null)
        {
            // Prefer the live selfie (matches real camera conditions for later per-shift checks);
            // fall back to the portrait extracted from the ID document.
            var mediaUrl = decision.SelfieImageUrl ?? decision.PortraitImageUrl;
            if (mediaUrl != null)
                record.ReferenceSelfiePath = await StoreReferenceSelfieAsync(record, mediaUrl, cancellationToken);
            else
                _logger.LogWarning("[KYC] Approved decision for session {SessionId} has no selfie/portrait URL", record.DiditSessionId);
        }

        await ApplyStatusAsync(record, decision.Status, decision.StatusReason, cancellationToken);
    }

    private async Task ApplyStatusAsync(DriverVerification record, DriverVerificationStatus status, string? reason, CancellationToken cancellationToken)
    {
        var wasTerminalAlready = record.Status == status;
        record.Status = status;
        record.StatusReason = reason ?? record.StatusReason;

        if (status is DriverVerificationStatus.Approved or DriverVerificationStatus.Declined
            or DriverVerificationStatus.Abandoned or DriverVerificationStatus.Expired)
        {
            record.CompletedAt ??= DateTime.UtcNow;
        }

        if (wasTerminalAlready)
            return;

        if (status == DriverVerificationStatus.Approved)
        {
            _logger.LogInformation("[KYC] User {UserId} approved (session {SessionId}, faceMatch={FaceMatch}, liveness={Liveness})",
                record.UserId, record.DiditSessionId, record.FaceMatchScore, record.LivenessScore);
            if (_onVerified != null)
                await _onVerified.MarkVerifiedAsync(record.UserId, cancellationToken);
        }

        if (status is DriverVerificationStatus.Approved or DriverVerificationStatus.Declined)
            await PublishCompletedEventAsync(record, cancellationToken);
    }

    private async Task<string?> StoreReferenceSelfieAsync(DriverVerification record, string mediaUrl, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            await using (var stream = await _didit.DownloadMediaAsync(mediaUrl, cancellationToken))
            {
                if (stream == null)
                    return null;
                await using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }

            var objectName = $"{record.UserId}/{record.Id:N}.jpg";
            // Fresh stream per consumer — the storage client may read to end or dispose it.
            string pathOrUrl;
            await using (var uploadStream = new MemoryStream(bytes, writable: false))
            {
                pathOrUrl = await _fileStorage.UploadFileAsync(uploadStream, VerificationsBucket, objectName, "image/jpeg", cancellationToken);
            }

            // Pre-compute the face embedding used by per-shift checks (ShiftCheckService
            // backfills lazily if the face-match service is down right now).
            if (_faceMatch?.Enabled == true)
            {
                try
                {
                    await using var embedStream = new MemoryStream(bytes, writable: false);
                    var embedding = await _faceMatch.EmbedAsync(embedStream, cancellationToken);
                    if (embedding != null)
                        record.ReferenceEmbedding = JsonSerializer.Serialize(embedding);
                    else
                        _logger.LogWarning("[KYC] No face found in reference selfie for session {SessionId}", record.DiditSessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[KYC] Reference embedding computation failed for session {SessionId}", record.DiditSessionId);
                }
            }

            // Same stored-reference convention as driver application documents.
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{VerificationsBucket}:{objectName}";
            return pathOrUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KYC] Failed to store reference selfie for session {SessionId}", record.DiditSessionId);
            return null;
        }
    }

    private async Task PublishCompletedEventAsync(DriverVerification record, CancellationToken cancellationToken)
    {
        if (_publishEndpoint == null)
            return;
        try
        {
            await _publishEndpoint.Publish(new DriverKycCompletedEvent(
                record.Id,
                record.UserId,
                record.Status.ToString(),
                record.FaceMatchScore,
                record.LivenessScore,
                record.CompletedAt ?? DateTime.UtcNow), cancellationToken);
        }
        catch (Exception ex)
        {
            // Broker outage must not fail webhook processing; back-office reconciles later.
            _logger.LogError(ex, "[KYC] Failed to publish DriverKycCompletedEvent for verification {VerificationId}", record.Id);
        }
    }

    private Task<DriverVerification?> GetLatestAsync(string userId, CancellationToken cancellationToken) =>
        _db.DriverVerifications
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
