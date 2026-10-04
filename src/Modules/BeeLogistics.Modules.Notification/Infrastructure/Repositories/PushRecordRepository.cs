using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Modules.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Infrastructure.Repositories;

public class PushRecordRepository : IPushRecordRepository
{
    private readonly NotificationDbContext _context;

    public PushRecordRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task<(IReadOnlyList<PushNotificationRecord> Items, int TotalCount)> GetPagedAsync(
        PushStatus? status = null,
        string? search = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _context.PushNotificationRecords.AsQueryable();

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Title.Contains(search) || (p.TargetValue != null && p.TargetValue.Contains(search)));

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<PushRecordStats> GetStatsAsync(CancellationToken ct = default)
    {
        var byStatus = await _context.PushNotificationRecords
            .GroupBy(p => p.Status)
            .Select(g => new PushStatusCount(g.Key.ToString(), g.Count()))
            .ToListAsync(ct);

        var total = await _context.PushNotificationRecords.CountAsync(ct);
        var last24Hours = await _context.PushNotificationRecords
            .Where(p => p.CreatedAt >= DateTime.UtcNow.AddHours(-24))
            .CountAsync(ct);

        return new PushRecordStats(total, last24Hours, byStatus);
    }

    public async Task<PushNotificationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PushNotificationRecords.FindAsync(new object[] { id }, ct);
    }

    public async Task AddAsync(PushNotificationRecord record, CancellationToken ct = default)
    {
        _context.PushNotificationRecords.Add(record);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PushNotificationRecord record, CancellationToken ct = default)
    {
        _context.PushNotificationRecords.Update(record);
        await _context.SaveChangesAsync(ct);
    }
}
