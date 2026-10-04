using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using BeeLogistics.Modules.Notification.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Infrastructure.Repositories;

public class EmailRecordRepository : IEmailRecordRepository
{
    private readonly NotificationDbContext _context;

    public EmailRecordRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task<(IReadOnlyList<EmailRecord> Items, int TotalCount)> GetPagedAsync(
        EmailStatus? status = null,
        string? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _context.EmailRecords.AsQueryable();

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        if (!string.IsNullOrEmpty(to))
            query = query.Where(e => e.To.Contains(to));

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<EmailRecordStats> GetStatsAsync(CancellationToken ct = default)
    {
        var byStatus = await _context.EmailRecords
            .GroupBy(e => e.Status)
            .Select(g => new EmailStatusCount(g.Key.ToString(), g.Count()))
            .ToListAsync(ct);

        var total = await _context.EmailRecords.CountAsync(ct);
        var last24Hours = await _context.EmailRecords
            .Where(e => e.CreatedAt >= DateTime.UtcNow.AddHours(-24))
            .CountAsync(ct);

        return new EmailRecordStats(total, last24Hours, byStatus);
    }
}
