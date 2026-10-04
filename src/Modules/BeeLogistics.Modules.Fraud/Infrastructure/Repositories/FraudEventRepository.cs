using BeeLogistics.Modules.Fraud.Application.Interfaces;
using BeeLogistics.Modules.Fraud.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Fraud.Infrastructure.Repositories;

public class FraudEventRepository : IFraudEventRepository
{
    private readonly FraudDbContext _context;

    public FraudEventRepository(FraudDbContext context)
    {
        _context = context;
    }

    public void Add(FraudEvent evt) => _context.FraudEvents.Add(evt);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    public async Task<int> CountDeliveryCompletedByDriverAsync(Guid driverId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _context.FraudEvents
            .CountAsync(e => e.EventType == FraudEventType.DeliveryCompleted && e.DriverId == driverId && e.OccurredAt >= from && e.OccurredAt <= to, ct);
    }

    public async Task<int> CountOrderCreatedByCustomerAsync(Guid customerId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _context.FraudEvents
            .CountAsync(e => e.EventType == FraudEventType.OrderCreated && e.CustomerId == customerId && e.OccurredAt >= from && e.OccurredAt <= to, ct);
    }

    public async Task<int> CountDistinctUsersByDeviceAsync(string deviceId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var query = _context.FraudEvents
            .Where(e => e.EventType == FraudEventType.UserRegistered && e.DeviceId == deviceId);
        if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAt <= to.Value);
        return await query.Select(e => e.UserId).Distinct().CountAsync(ct);
    }

    public async Task<FraudEvent?> GetLatestLocationByDriverAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.FraudEvents
            .Where(e => e.EventType == FraudEventType.LocationUpdated && e.DriverId == driverId)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<FraudEvent>> GetEventsAsync(DateTime? from, DateTime? to, FraudEventType? eventType, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _context.FraudEvents.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAt <= to.Value);
        if (eventType.HasValue) query = query.Where(e => e.EventType == eventType.Value);
        return await query
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
    }

    public async Task<int> CountEventsAsync(DateTime? from, DateTime? to, FraudEventType? eventType, CancellationToken ct = default)
    {
        var query = _context.FraudEvents.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAt <= to.Value);
        if (eventType.HasValue) query = query.Where(e => e.EventType == eventType.Value);
        return await query.CountAsync(ct);
    }
}
