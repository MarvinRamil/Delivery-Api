using System.Text.Json;
using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using BeeLogistics.Modules.Verification.Domain;
using BeeLogistics.Modules.Verification.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Application.Services;

/// <summary>
/// Customer identity verification via Didit KYC. Same hosted flow as the driver
/// <see cref="KycService"/> but persists to the separate CustomerVerifications table and
/// omits driver-only side effects: no reference embedding (customers have no per-shift
/// checks) and no DriverKycCompletedEvent (that drives back-office driver review). It still
/// stores the approved reference selfie via <see cref="IFileStorageService"/> for audit,
/// same as the driver flow.
/// On approval it stamps LivenessVerifiedAt via <see cref="IOnLivenessVerified"/>, which
/// is the customer onboarding gate.
/// </summary>
public class CustomerKycService : ICustomerKycService
{
    /// <summary>Didit vendor_data tag that routes webhooks to this (customer) service.</summary>
    public const string VendorPrefix = "customer:";

    public const string VerificationsBucket = "customer-verifications";

    private readonly VerificationDbContext _db;
    private readonly IDiditApiClient _didit;
    private readonly IFileStorageService _fileStorage;
    private readonly DiditOptions _options;
    private readonly ILogger<CustomerKycService> _logger;
    private readonly IOnLivenessVerified? _onVerified;

    public CustomerKycService(
        VerificationDbContext db,
        IDiditApiClient didit,
        IFileStorageService fileStorage,
        IOptions<DiditOptions> options,
        ILogger<CustomerKycService> logger,
        IOnLivenessVerified? onVerified = null)
    {
        _db = db;
        _didit = didit;
        _fileStorage = fileStorage;
        _options = options.Value;
        _logger = logger;
        _onVerified = onVerified;
    }

    public async Task<CreateKycSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(userId, cancellationToken);
        if (latest?.Status is DriverVerificationStatus.Approved or DriverVerificationStatus.Legacy)
            throw new InvalidOperationException("Identity is already verified.");

        // vendor_data carries the user id AND the "customer:" tag so the shared webhook can
        // route the result to this service (and re-create the row if it were ever lost).
        // Runs the customer-specific Didit workflow (falls back to the driver workflow when unset).
        var created = await _didit.CreateSessionAsync(
            VendorPrefix + userId, _options.CustomerWorkflowId, cancellationToken);

        // Didit hands back the *same* session_id when a customer re-engages a session that never
        // reached a terminal status, so the insert must be idempotent against the unique index
        // on DiditSessionId. Reuse leaves the row untouched: a webhook or poll reconcile may
        // already have advanced it past Pending.
        var existing = await _db.CustomerVerifications
            .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);

        if (existing != null)
        {
            _logger.LogInformation("[CustomerKYC] Reusing open Didit session {SessionId} for customer {UserId} (status {Status})",
                created.SessionId, userId, existing.Status);
            return new CreateKycSessionResult(created.SessionId, created.Url);
        }

        _db.CustomerVerifications.Add(new CustomerVerification
        {
            UserId = userId,
            DiditSessionId = created.SessionId,
            Status = DriverVerificationStatus.Pending
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("[CustomerKYC] Didit session {SessionId} created for customer {UserId}", created.SessionId, userId);
        }
        catch (DbUpdateException)
        {
            // A concurrent request for the same reused session won the insert. Drop our pending
            // Added entity (it would be retried on any later SaveChanges) and confirm the row.
            _db.ChangeTracker.Clear();
            var raced = await _db.CustomerVerifications
                .FirstOrDefaultAsync(v => v.DiditSessionId == created.SessionId, cancellationToken);
            if (raced == null)
                throw;

            _logger.LogInformation("[CustomerKYC] Concurrent insert for session {SessionId}; reusing", created.SessionId);
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
                _logger.LogWarning(ex, "[CustomerKYC] Poll reconcile failed for session {SessionId}", latest.DiditSessionId);
            }
        }

        return new KycStatusResult(latest.Status.ToString(), latest.FaceMatchScore, latest.LivenessScore, latest.StatusReason, latest.CompletedAt);
    }

    public async Task ProcessWebhookAsync(string sessionId, string? vendorData, string? diditStatus, CancellationToken cancellationToken = default)
    {
        var record = await _db.CustomerVerifications.FirstOrDefaultAsync(v => v.DiditSessionId == sessionId, cancellationToken);
        if (record == null)
        {
            // Session unknown locally (e.g. row lost after create). vendor_data carries "customer:{userId}".
            var userId = StripPrefix(vendorData);
            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("[CustomerKYC] Webhook for unknown session {SessionId} without usable vendor_data — ignored", sessionId);
                return;
            }
            record = new CustomerVerification { UserId = userId, DiditSessionId = sessionId };
            _db.CustomerVerifications.Add(record);
        }

        var mapped = DiditDecisionParser.MapStatus(diditStatus);

        // Idempotency: webhooks are retried; skip if nothing would change.
        if (record.Status == mapped && mapped != DriverVerificationStatus.Approved)
        {
            _logger.LogDebug("[CustomerKYC] Webhook for session {SessionId} is a no-op (status {Status})", sessionId, mapped);
            return;
        }

        if (mapped is DriverVerificationStatus.Approved or DriverVerificationStatus.Declined or DriverVerificationStatus.InReview)
        {
            // Re-fetch the decision from the API to protect against a forged/partial payload.
            using var decision = await _didit.GetDecisionAsync(sessionId, cancellationToken);
            if (decision != null)
            {
                await ApplyDecisionAsync(record, decision.RootElement, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[CustomerKYC] Decision fetch returned nothing for session {SessionId}; applying webhook status only", sessionId);
                await ApplyStatusAsync(record, mapped, null, cancellationToken);
            }
        }
        else
        {
            await ApplyStatusAsync(record, mapped, null, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyDecisionAsync(CustomerVerification record, JsonElement decisionRoot, CancellationToken cancellationToken)
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
            // Prefer the live selfie (matches real camera conditions); fall back to the portrait
            // extracted from the ID document. Kept for audit only - no embedding/per-shift use.
            var mediaUrl = decision.SelfieImageUrl ?? decision.PortraitImageUrl;
            if (mediaUrl != null)
                record.ReferenceSelfiePath = await StoreReferenceSelfieAsync(record, mediaUrl, cancellationToken);
            else
                _logger.LogWarning("[CustomerKYC] Approved decision for session {SessionId} has no selfie/portrait URL", record.DiditSessionId);
        }

        await ApplyStatusAsync(record, decision.Status, decision.StatusReason, cancellationToken);
    }

    private async Task<string?> StoreReferenceSelfieAsync(CustomerVerification record, string mediaUrl, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _didit.DownloadMediaAsync(mediaUrl, cancellationToken);
            if (stream == null)
                return null;

            var objectName = $"{record.UserId}/{record.Id:N}.jpg";
            var pathOrUrl = await _fileStorage.UploadFileAsync(stream, VerificationsBucket, objectName, "image/jpeg", cancellationToken);

            // Same stored-reference convention as the driver flow.
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return $"s3:{VerificationsBucket}:{objectName}";
            return pathOrUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CustomerKYC] Failed to store reference selfie for session {SessionId}", record.DiditSessionId);
            return null;
        }
    }

    private async Task ApplyStatusAsync(CustomerVerification record, DriverVerificationStatus status, string? reason, CancellationToken cancellationToken)
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
            _logger.LogInformation("[CustomerKYC] Customer {UserId} approved (session {SessionId}, faceMatch={FaceMatch}, liveness={Liveness})",
                record.UserId, record.DiditSessionId, record.FaceMatchScore, record.LivenessScore);
            if (_onVerified != null)
                await _onVerified.MarkVerifiedAsync(record.UserId, cancellationToken);
        }
    }

    private static string? StripPrefix(string? vendorData) =>
        vendorData != null && vendorData.StartsWith(VendorPrefix, StringComparison.Ordinal)
            ? vendorData[VendorPrefix.Length..]
            : vendorData;

    private Task<CustomerVerification?> GetLatestAsync(string userId, CancellationToken cancellationToken) =>
        _db.CustomerVerifications
            .Where(v => v.UserId == userId)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
