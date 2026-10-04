using BeeLogistics.Modules.Notification.Domain;

namespace BeeLogistics.Modules.Notification.Application.Interfaces;

/// <summary>
/// Repository for PushNotificationRecord. Keeps data access in Infrastructure; the
/// dispatcher, consumer, and history controller use it via the Application layer.
/// </summary>
public interface IPushRecordRepository
{
    /// <summary>Get paged push records with optional filters. Returns items and total count.</summary>
    Task<(IReadOnlyList<PushNotificationRecord> Items, int TotalCount)> GetPagedAsync(
        PushStatus? status = null,
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);

    /// <summary>Get aggregate stats: total count, last 24h count, and counts by status.</summary>
    Task<PushRecordStats> GetStatsAsync(CancellationToken ct = default);

    Task<PushNotificationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(PushNotificationRecord record, CancellationToken ct = default);

    Task UpdateAsync(PushNotificationRecord record, CancellationToken ct = default);
}

/// <summary>DTO returned by GetStatsAsync.</summary>
public record PushRecordStats(
    int Total,
    int Last24Hours,
    IReadOnlyList<PushStatusCount> ByStatus);

public record PushStatusCount(string Status, int Count);
