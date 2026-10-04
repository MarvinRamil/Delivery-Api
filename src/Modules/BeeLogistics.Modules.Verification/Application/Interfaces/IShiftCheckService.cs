using BeeLogistics.Modules.Verification.Application.DTOs;

namespace BeeLogistics.Modules.Verification.Application.Interfaces;

public interface IShiftCheckService
{
    /// <summary>Start a per-shift face check. Throws InvalidOperationException when the driver has no face reference.</summary>
    Task<CreateShiftCheckSessionResult> CreateSessionAsync(string userId, CancellationToken cancellationToken = default);

    Task<SubmitShiftCheckImageResult> SubmitImageAsync(string sessionId, string userId, string direction, Stream imageStream, CancellationToken cancellationToken = default);

    Task<LivenessSessionStatusResult> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default);
}
