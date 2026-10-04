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
/// Per-shift face check: the driver repeats the head-pose challenge and each frame is
/// face-matched (self-hosted InsightFace) against the reference selfie stored at KYC
/// approval, optionally after the YOLO anti-spoofing check. Passing marks
/// LastFaceCheckAt via <see cref="IOnShiftCheckPassed"/> which unlocks going online.
/// </summary>
public class ShiftCheckService : IShiftCheckService, IShiftCheckGate
{
    private readonly IShiftCheckSessionStore _store;
    private readonly VerificationDbContext _db;
    private readonly IFaceMatchApiClient _faceMatch;
    private readonly ILivenessApiClient _liveness;
    private readonly IFileStorageService _fileStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ShiftCheckOptions _options;
    private readonly FaceMatchOptions _faceMatchOptions;
    private readonly ILogger<ShiftCheckService> _logger;
    private readonly IOnShiftCheckPassed? _onPassed;

    public ShiftCheckService(
        IShiftCheckSessionStore store,
        VerificationDbContext db,
        IFaceMatchApiClient faceMatch,
        ILivenessApiClient liveness,
        IFileStorageService fileStorage,
        IHttpClientFactory httpClientFactory,
        IOptions<ShiftCheckOptions> options,
        IOptions<FaceMatchOptions> faceMatchOptions,
        ILogger<ShiftCheckService> logger,
        IOnShiftCheckPassed? onPassed = null)
    {
        _store = store;
        _db = db;
        _faceMatch = faceMatch;
        _liveness = liveness;
        _fileStorage = fileStorage;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _faceMatchOptions = faceMatchOptions.Value;
        _logger = logger;
        _onPassed = onPassed;
    }

    // ----- IShiftCheckGate -----

    public async Task<bool> RequiresCheckAsync(string userId, DateTime? lastFaceCheckAt, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _options.ShadowMode || !_faceMatch.Enabled)
            return false;

        if (lastFaceCheckAt != null && DateTime.UtcNow - lastFaceCheckAt.Value < TimeSpan.FromHours(_options.MaxAgeHours))
            return false;

        // Legacy drivers (verified before Didit KYC) have no reference selfie — skip them.
        var reference = await GetReferenceVerificationAsync(userId, cancellationToken);
        return reference != null;
    }

    // ----- IShiftCheckService -----

    public async Task<CreateShiftCheckSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        var embedding = await EnsureReferenceEmbeddingAsync(userId, cancellationToken);
        if (embedding == null)
            throw new InvalidOperationException("No verified face reference on file. Complete identity verification first.");

        var sessionId = InMemoryLivenessSessionStore.NewSessionId();
        var directions = LivenessDirection.RandomSequence(Math.Clamp(_options.DirectionCount, 1, 4));
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.SessionExpiryMinutes);

        _store.Set(new LivenessSession
        {
            SessionId = sessionId,
            UserId = userId,
            Directions = directions,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        });

        return new CreateShiftCheckSessionResult(sessionId, directions, expiresAt);
    }

    public async Task<SubmitShiftCheckImageResult> SubmitImageAsync(string sessionId, string userId, string direction, Stream imageStream, CancellationToken cancellationToken = default)
    {
        var session = _store.Get(sessionId);
        if (session == null || DateTime.UtcNow > session.ExpiresAt)
        {
            _store.Remove(sessionId);
            return new SubmitShiftCheckImageResult(false, false, Array.Empty<string>(), "Session not found or expired.");
        }

        if (!string.Equals(session.UserId, userId, StringComparison.Ordinal))
            return new SubmitShiftCheckImageResult(false, false, Array.Empty<string>(), "Session does not belong to this user.");

        if (!session.Directions.Contains(direction, StringComparer.OrdinalIgnoreCase))
            return new SubmitShiftCheckImageResult(false, false, Remaining(session), "Invalid direction for this session.");

        if (session.DirectionsPassed.Contains(direction, StringComparer.OrdinalIgnoreCase))
        {
            var rem = Remaining(session);
            return new SubmitShiftCheckImageResult(true, rem.Length == 0, rem);
        }

        var embedding = await EnsureReferenceEmbeddingAsync(userId, cancellationToken);
        if (embedding == null)
            return new SubmitShiftCheckImageResult(false, false, Remaining(session), "No verified face reference on file.");

        // The frame may be read twice (anti-spoofing + face match) — buffer it.
        await using var buffered = new MemoryStream();
        await imageStream.CopyToAsync(buffered, cancellationToken);

        try
        {
            if (_options.UseLiveness)
            {
                buffered.Position = 0;
                var live = await CheckLivenessSafeAsync(buffered, cancellationToken);
                if (live == false)
                {
                    await RecordAttemptAsync(userId, null, passed: false, cancellationToken);
                    return new SubmitShiftCheckImageResult(false, false, Remaining(session), "Face check failed. Please try again in good lighting.");
                }
            }

            buffered.Position = 0;
            var score = await _faceMatch.VerifyAsync(buffered, embedding, cancellationToken);
            var passed = score != null && score.Value >= _faceMatchOptions.MatchThreshold;

            _logger.LogInformation("[ShiftCheck] session {SessionId} direction {Direction} score={Score:F3} threshold={Threshold} passed={Passed}",
                sessionId, direction, score ?? -1, _faceMatchOptions.MatchThreshold, passed);

            if (!passed)
            {
                await RecordAttemptAsync(userId, score, passed: false, cancellationToken);
                var err = score == null
                    ? "No face detected. Position your face within the frame."
                    : "Face didn't match our records. Make sure you're the registered driver.";
                return new SubmitShiftCheckImageResult(false, false, Remaining(session), err, score);
            }

            session.DirectionsPassed.Add(direction);
            var remaining = Remaining(session);
            var allPassed = remaining.Length == 0;

            if (allPassed)
            {
                _store.Remove(sessionId);
                await RecordAttemptAsync(userId, score, passed: true, cancellationToken);
                if (_onPassed != null)
                    await _onPassed.MarkPassedAsync(userId, cancellationToken);
                _logger.LogInformation("[ShiftCheck] All directions passed for user {UserId}", userId);
            }
            else
            {
                _store.Set(session);
            }

            return new SubmitShiftCheckImageResult(true, allPassed, remaining, null, score);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShiftCheck] Check failed sessionId={SessionId} direction={Direction}", sessionId, direction);
            return new SubmitShiftCheckImageResult(false, false, Remaining(session), "Face check failed: " + ex.Message);
        }
    }

    public Task<LivenessSessionStatusResult> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = _store.Get(sessionId);
        if (session == null)
            return Task.FromResult(new LivenessSessionStatusResult(LivenessSessionStatusResult.NotFound, Array.Empty<string>(), Array.Empty<string>()));

        var passed = session.DirectionsPassed.ToArray();
        var remaining = Remaining(session);
        var status = remaining.Length == 0 ? LivenessSessionStatusResult.Passed : LivenessSessionStatusResult.InProgress;
        return Task.FromResult(new LivenessSessionStatusResult(status, passed, remaining));
    }

    // ----- internals -----

    private static string[] Remaining(LivenessSession session) =>
        session.Directions.Except(session.DirectionsPassed).ToArray();

    private async Task<bool?> CheckLivenessSafeAsync(Stream frame, CancellationToken cancellationToken)
    {
        try
        {
            return await _liveness.CheckLivenessAsync(frame, cancellationToken);
        }
        catch (Exception ex)
        {
            // Anti-spoofing service down — degrade to face-match-only rather than blocking drivers.
            _logger.LogWarning(ex, "[ShiftCheck] Liveness stage unavailable; continuing with face match only");
            return null;
        }
    }

    private Task<DriverVerification?> GetReferenceVerificationAsync(string userId, CancellationToken cancellationToken) =>
        _db.DriverVerifications
            .Where(v => v.UserId == userId
                && v.Status == DriverVerificationStatus.Approved
                && (v.ReferenceEmbedding != null || v.ReferenceSelfiePath != null))
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Reference embedding for the driver, computing it from the stored reference selfie
    /// if it wasn't computed at KYC approval time (e.g. face-match service was down).
    /// </summary>
    private async Task<float[]?> EnsureReferenceEmbeddingAsync(string userId, CancellationToken cancellationToken)
    {
        var verification = await GetReferenceVerificationAsync(userId, cancellationToken);
        if (verification == null)
            return null;

        if (verification.ReferenceEmbedding != null)
        {
            try
            {
                return JsonSerializer.Deserialize<float[]>(verification.ReferenceEmbedding);
            }
            catch (JsonException)
            {
                _logger.LogWarning("[ShiftCheck] Corrupt reference embedding for user {UserId}; recomputing", userId);
            }
        }

        if (verification.ReferenceSelfiePath == null || !_faceMatch.Enabled)
            return null;

        var embedding = await ComputeEmbeddingFromStorageAsync(verification.ReferenceSelfiePath, cancellationToken);
        if (embedding == null)
            return null;

        verification.ReferenceEmbedding = JsonSerializer.Serialize(embedding);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[ShiftCheck] Backfilled reference embedding for user {UserId}", userId);
        return embedding;
    }

    private async Task<float[]?> ComputeEmbeddingFromStorageAsync(string storedReference, CancellationToken cancellationToken)
    {
        try
        {
            // Stored-reference convention (same as driver documents): "s3:bucket:objectKey" or a local path.
            string url;
            if (storedReference.StartsWith("s3:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = storedReference.Split(':', 3);
                if (parts.Length != 3)
                    return null;
                url = await _fileStorage.GetPresignedUrlWithFullKeyAsync(parts[1], parts[2], 300, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[ShiftCheck] Cannot backfill embedding from non-S3 reference selfie");
                return null;
            }

            using var http = _httpClientFactory.CreateClient();
            await using var stream = await http.GetStreamAsync(url, cancellationToken);
            await using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken);
            buffered.Position = 0;
            return await _faceMatch.EmbedAsync(buffered, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShiftCheck] Failed to compute embedding from stored selfie");
            return null;
        }
    }

    private async Task RecordAttemptAsync(string userId, double? score, bool passed, CancellationToken cancellationToken)
    {
        try
        {
            _db.ShiftFaceChecks.Add(new ShiftFaceCheck { UserId = userId, MatchScore = score, Passed = passed });
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShiftCheck] Failed to record attempt for user {UserId}", userId);
        }
    }
}
