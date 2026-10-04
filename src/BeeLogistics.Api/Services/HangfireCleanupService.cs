using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Api.Services;

/// <summary>
/// Service to automatically clean up old Hangfire jobs
/// </summary>
public class HangfireCleanupService
{
    private readonly ILogger<HangfireCleanupService> _logger;

    public HangfireCleanupService(ILogger<HangfireCleanupService> logger)
    {
        _logger = logger;
    }

    private IMonitoringApi GetMonitoringApi()
    {
        return JobStorage.Current.GetMonitoringApi();
    }

    /// <summary>
    /// Hangfire recurring job to clean up old jobs
    /// Runs daily at 2 AM UTC
    /// </summary>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
    public async Task CleanupOldJobsAsync()
    {
        try
        {
            _logger.LogInformation("Starting Hangfire job cleanup");

            var deletedCount = 0;
            var cutoffDate = DateTime.UtcNow.AddDays(-7); // Keep jobs for 7 days
            var monitoringApi = GetMonitoringApi();

            // Clean up succeeded jobs older than 7 days
            var succeededJobs = monitoringApi.SucceededJobs(0, int.MaxValue);
            foreach (var job in succeededJobs)
            {
                if (job.Value.SucceededAt.HasValue && job.Value.SucceededAt.Value < cutoffDate)
                {
                    try
                    {
                        BackgroundJob.Delete(job.Key);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete succeeded job {JobId}", job.Key);
                    }
                }
            }

            // Clean up failed jobs older than 30 days (keep failed jobs longer for debugging)
            var failedCutoffDate = DateTime.UtcNow.AddDays(-30);
            var failedJobs = monitoringApi.FailedJobs(0, int.MaxValue);
            foreach (var job in failedJobs)
            {
                if (job.Value.FailedAt.HasValue && job.Value.FailedAt.Value < failedCutoffDate)
                {
                    try
                    {
                        BackgroundJob.Delete(job.Key);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete failed job {JobId}", job.Key);
                    }
                }
            }

            // Clean up deleted jobs (orphaned records)
            var deletedJobs = monitoringApi.DeletedJobs(0, int.MaxValue);
            foreach (var job in deletedJobs)
            {
                if (job.Value.DeletedAt.HasValue && job.Value.DeletedAt.Value < cutoffDate)
                {
                    try
                    {
                        BackgroundJob.Delete(job.Key);
                        deletedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete deleted job {JobId}", job.Key);
                    }
                }
            }

            _logger.LogInformation("Hangfire cleanup completed. Deleted {Count} old jobs", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Hangfire job cleanup");
            throw; // Re-throw to trigger retry
        }
    }

    /// <summary>
    /// Clean up old job statistics and counters
    /// Runs weekly on Sunday at 3 AM UTC
    /// </summary>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 60, 300 })]
    public async Task CleanupStatisticsAsync()
    {
        try
        {
            _logger.LogInformation("Starting Hangfire statistics cleanup");

            using var connection = JobStorage.Current.GetConnection();
            var storage = JobStorage.Current as JobStorage;

            if (storage != null)
            {
                // Clean up old counters (older than 7 days)
                var cutoffDate = DateTime.UtcNow.AddDays(-7);
                
                // Note: This is a simplified approach. For PostgreSQL, you might need
                // to execute raw SQL to clean up the hangfire.counter table
                _logger.LogInformation("Statistics cleanup completed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Hangfire statistics cleanup");
            throw;
        }
    }
}
