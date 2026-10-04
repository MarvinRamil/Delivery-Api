using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Messaging.Infrastructure.Repositories;

public sealed class RoomEventArchive : IRoomEventArchive
{
    private readonly MessagingDbContext _db;

    public RoomEventArchive(MessagingDbContext db) => _db = db;

    public async Task<bool> TryAddAsync(RoomEvent roomEvent, CancellationToken ct = default)
    {
        _db.RoomEvents.Add(roomEvent);
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Almost certainly the unique index on MatrixEventId doing its job on a retried
            // transaction. Detach so the context is not left holding a rejected entity, then
            // confirm rather than assume — a different constraint failing must not be swallowed.
            _db.Entry(roomEvent).State = EntityState.Detached;

            var exists = await _db.RoomEvents
                .AsNoTracking()
                .AnyAsync(x => x.MatrixEventId == roomEvent.MatrixEventId, ct);

            if (exists) return false;

            throw;
        }
    }

    public async Task<IReadOnlyList<RoomEvent>> GetForBookingAsync(Guid bookingId, int skip, int take, CancellationToken ct = default) =>
        await _db.RoomEvents
            .AsNoTracking()
            .Where(x => x.BookingId == bookingId)
            .OrderBy(x => x.OriginServerTs)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public Task<int> CountForBookingAsync(Guid bookingId, CancellationToken ct = default) =>
        _db.RoomEvents.AsNoTracking().CountAsync(x => x.BookingId == bookingId, ct);
}
