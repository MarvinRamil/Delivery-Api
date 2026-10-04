using BeeLogistics.Modules.Verification.Application.DTOs;

namespace BeeLogistics.Modules.Verification.Application.Interfaces;

public interface ILivenessSessionService
{
    /// <summary>Creates a new liveness session and returns a randomized list of directions (e.g. left, right, up, down).</summary>
    Task<CreateLivenessSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Submits an image for one direction; returns whether this direction passed and overall status.</summary>
    Task<SubmitLivenessImageResult> SubmitImageAsync(string sessionId, string direction, Stream imageStream, CancellationToken cancellationToken = default);

    /// <summary>Gets the current session result (in progress, passed, or failed).</summary>
    Task<LivenessSessionStatusResult> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default);
}
