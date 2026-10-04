using BeeLogistics.Modules.Messaging.Application.Interfaces;
using BeeLogistics.Modules.Messaging.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Messaging.Infrastructure.Repositories;

public sealed class BookingRoomRepository : IBookingRoomRepository
{
    private readonly MessagingDbContext _db;

    public BookingRoomRepository(MessagingDbContext db) => _db = db;

    public Task<BookingRoom?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default) =>
        _db.BookingRooms.FirstOrDefaultAsync(x => x.BookingId == bookingId, ct);

    public Task<BookingRoom?> GetByRoomIdAsync(string roomId, CancellationToken ct = default) =>
        _db.BookingRooms.FirstOrDefaultAsync(x => x.RoomId == roomId, ct);

    public async Task<BookingRoom> AddOrGetAsync(BookingRoom room, CancellationToken ct = default)
    {
        _db.BookingRooms.Add(room);
        try
        {
            await _db.SaveChangesAsync(ct);
            return room;
        }
        catch (DbUpdateException)
        {
            // A redelivered provisioning message. The unique index on BookingId is what turns a
            // duplicate room into a duplicate-key error instead of a second room, and this turns
            // that error back into the answer the caller wanted.
            _db.Entry(room).State = EntityState.Detached;

            var existing = await GetByBookingIdAsync(room.BookingId, ct);
            if (existing is not null) return existing;

            throw;
        }
    }

    public async Task<IReadOnlyList<BookingRoom>> GetDueForFreezeAsync(DateTime endedBefore, int limit, CancellationToken ct = default) =>
        await _db.BookingRooms
            .Where(x => x.State == BookingRoomState.Active && x.EndedAt != null && x.EndedAt < endedBefore)
            .OrderBy(x => x.EndedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BookingRoom>> GetDueForPurgeAsync(DateTime frozenBefore, int limit, CancellationToken ct = default) =>
        await _db.BookingRooms
            .Where(x => x.State == BookingRoomState.Frozen && x.FrozenAt != null && x.FrozenAt < frozenBefore)
            .OrderBy(x => x.FrozenAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetExistingBookingIdsAsync(IReadOnlyList<Guid> bookingIds, CancellationToken ct = default) =>
        await _db.BookingRooms
            .AsNoTracking()
            .Where(x => bookingIds.Contains(x.BookingId))
            .Select(x => x.BookingId)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
