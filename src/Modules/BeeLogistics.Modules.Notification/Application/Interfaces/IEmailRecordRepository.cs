using BeeLogistics.Modules.Notification.Domain;

namespace BeeLogistics.Modules.Notification.Application.Interfaces;

/// <summary>
/// Repository for EmailRecord. Keeps data access in Infrastructure; Presentation uses this via Application layer.
/// </summary>
public interface IEmailRecordRepository
{
    /// <summary>Get paged email records with optional filters. Returns items and total count.</summary>
    Task<(IReadOnlyList<EmailRecord> Items, int TotalCount)> GetPagedAsync(
        EmailStatus? status = null,
        string? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);

    /// <summary>Get aggregate stats: total count, last 24h count, and counts by status.</summary>
    Task<EmailRecordStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>DTO returned by GetStatsAsync.</summary>
public record EmailRecordStats(
    int Total,
    int Last24Hours,
    IReadOnlyList<EmailStatusCount> ByStatus);

public record EmailStatusCount(string Status, int Count);
