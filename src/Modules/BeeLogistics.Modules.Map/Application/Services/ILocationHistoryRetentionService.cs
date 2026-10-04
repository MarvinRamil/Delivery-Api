namespace BeeLogistics.Modules.Map.Application.Services;

/// <summary>
/// Ages out driver location trail points past the configured retention window.
///
/// Separate from the Hangfire job that drives it so the behaviour is reachable by tests: the job
/// lives in the API project, which the test project does not reference, and this deletes data.
/// </summary>
public interface ILocationHistoryRetentionService
{
    /// <param name="nowUtc">Reference time, injected so the window is testable without waiting.</param>
    /// <returns>Rows deleted.</returns>
    Task<int> PurgeAsync(DateTime nowUtc, CancellationToken ct = default);
}
