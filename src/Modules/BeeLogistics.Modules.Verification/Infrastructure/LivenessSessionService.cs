using BeeLogistics.Modules.Verification.Application.DTOs;
using BeeLogistics.Modules.Verification.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeeLogistics.Modules.Verification.Infrastructure;

public class LivenessSessionService : ILivenessSessionService
{
    private readonly ILivenessSessionStore _store;
    private readonly ILivenessApiClient _api;
    private readonly IOnLivenessVerified? _onVerified;
    private readonly LivenessOptions _options;
    private readonly ILogger<LivenessSessionService> _logger;

    public LivenessSessionService(
        ILivenessSessionStore store,
        ILivenessApiClient api,
        IOptions<LivenessOptions> options,
        ILogger<LivenessSessionService> logger,
        IOnLivenessVerified? onVerified = null)
    {
        _store = store;
        _api = api;
        _onVerified = onVerified;
        _options = options.Value;
        _logger = logger;
    }

    public Task<CreateLivenessSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        var sessionId = InMemoryLivenessSessionStore.NewSessionId();
        var directions = LivenessDirection.RandomSequence(4);
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.SessionExpiryMinutes);

        var session = new LivenessSession
        {
            SessionId = sessionId,
            UserId = userId,
            Directions = directions,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        };
        _store.Set(session);

        return Task.FromResult(new CreateLivenessSessionResult(sessionId, directions, expiresAt));
    }

    public async Task<SubmitLivenessImageResult> SubmitImageAsync(string sessionId, string direction, Stream imageStream, CancellationToken cancellationToken = default)
    {
        var session = _store.Get(sessionId);
        if (session == null)
        {
            _logger.LogWarning("[Liveness] SubmitImage session not found or expired sessionId={SessionId} direction={Direction}", sessionId, direction);
            return new SubmitLivenessImageResult(false, false, Array.Empty<string>(), "Session not found or expired.");
        }

        if (DateTime.UtcNow > session.ExpiresAt)
        {
            _store.Remove(sessionId);
            _logger.LogWarning("[Liveness] SubmitImage session expired sessionId={SessionId}", sessionId);
            return new SubmitLivenessImageResult(false, false, Array.Empty<string>(), "Session expired.");
        }

        if (!session.Directions.Contains(direction, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[Liveness] SubmitImage invalid direction sessionId={SessionId} direction={Direction}", sessionId, direction);
            return new SubmitLivenessImageResult(false, false, session.Directions.Except(session.DirectionsPassed).ToArray(), "Invalid direction for this session.");
        }

        if (session.DirectionsPassed.Contains(direction, StringComparer.OrdinalIgnoreCase))
        {
            var remaining = session.Directions.Except(session.DirectionsPassed).ToArray();
            return new SubmitLivenessImageResult(true, remaining.Length == 0, remaining);
        }

        bool passed;
        try
        {
            passed = await _api.CheckLivenessAsync(imageStream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Liveness] YOLO check failed sessionId={SessionId} direction={Direction}", sessionId, direction);
            return new SubmitLivenessImageResult(false, false, session.Directions.Except(session.DirectionsPassed).ToArray(), "Liveness check failed: " + ex.Message);
        }

        if (passed)
            session.DirectionsPassed.Add(direction);

        var remainingAfter = session.Directions.Except(session.DirectionsPassed).ToArray();
        var allPassed = remainingAfter.Length == 0;

        if (allPassed && _onVerified != null)
        {
            _store.Remove(sessionId);
            _logger.LogInformation("[Liveness] All directions passed, marking user verified userId={UserId} sessionId={SessionId}", session.UserId, sessionId);
            await _onVerified.MarkVerifiedAsync(session.UserId, cancellationToken);
        }
        else
            _store.Set(session);

        _logger.LogInformation("[Liveness] SubmitImage result sessionId={SessionId} direction={Direction} passed={Passed} allPassed={AllPassed} remaining={Remaining}",
            sessionId, direction, passed, allPassed, string.Join(",", remainingAfter));
        return new SubmitLivenessImageResult(passed, allPassed, remainingAfter);
    }

    public Task<LivenessSessionStatusResult> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = _store.Get(sessionId);
        if (session == null)
        {
            _logger.LogDebug("[Liveness] GetStatus session not found sessionId={SessionId}", sessionId);
            return Task.FromResult(new LivenessSessionStatusResult(LivenessSessionStatusResult.NotFound, Array.Empty<string>(), Array.Empty<string>()));
        }

        if (DateTime.UtcNow > session.ExpiresAt)
        {
            _store.Remove(sessionId);
            _logger.LogInformation("[Liveness] GetStatus session expired sessionId={SessionId}", sessionId);
            return Task.FromResult(new LivenessSessionStatusResult(LivenessSessionStatusResult.Expired, Array.Empty<string>(), Array.Empty<string>()));
        }

        var passed = session.DirectionsPassed.ToArray();
        var remaining = session.Directions.Except(session.DirectionsPassed).ToArray();
        var status = remaining.Length == 0 ? LivenessSessionStatusResult.Passed : LivenessSessionStatusResult.InProgress;
        _logger.LogDebug("[Liveness] GetStatus sessionId={SessionId} status={Status} passed={Passed} remaining={Remaining}", sessionId, status, string.Join(",", passed), string.Join(",", remaining));
        return Task.FromResult(new LivenessSessionStatusResult(status, passed, remaining));
    }
}
