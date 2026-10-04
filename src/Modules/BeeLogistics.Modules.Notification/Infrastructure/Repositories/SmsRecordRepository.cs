using BeeLogistics.Modules.Notification.Application.Interfaces;
using BeeLogistics.Modules.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Notification.Infrastructure.Repositories;

public class SmsRecordRepository : ISmsRecordRepository
{
    private readonly NotificationDbContext _context;

    public SmsRecordRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(SmsRecord record, CancellationToken ct = default)
    {
        _context.SmsRecords.Add(record);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<SmsRecord> Items, int TotalCount)> GetPagedAsync(
        SmsStatus? status = null,
        string? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _context.SmsRecords.AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        if (!string.IsNullOrEmpty(to))
        {
            query = query.Where(s => s.To.Contains(to));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }
}
