using BeeLogistics.Modules.Bookings.Application.Interfaces;
using BeeLogistics.Modules.Bookings.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Bookings.Infrastructure.Repositories;

public class FavouriteDriverRepository : IFavouriteDriverRepository
{
    private readonly BookingsDbContext _context;

    public FavouriteDriverRepository(BookingsDbContext context)
    {
        _context = context;
    }

    public async Task<FavouriteDriver?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.FavouriteDrivers.FindAsync(new object[] { id }, ct);
    }

    public async Task<FavouriteDriver?> GetByCustomerAndDriverAsync(Guid customerId, Guid driverId, CancellationToken ct = default)
    {
        return await _context.FavouriteDrivers
            .FirstOrDefaultAsync(fd => fd.CustomerId == customerId && fd.DriverId == driverId, ct);
    }

    public async Task<IReadOnlyList<FavouriteDriver>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
    {
        return await _context.FavouriteDrivers
            .Where(fd => fd.CustomerId == customerId)
            .OrderByDescending(fd => fd.AddedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FavouriteDriver>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.FavouriteDrivers
            .Where(fd => fd.DriverId == driverId)
            .OrderByDescending(fd => fd.AddedAt)
            .ToListAsync(ct);
    }

    public void Add(FavouriteDriver favouriteDriver)
    {
        _context.FavouriteDrivers.Add(favouriteDriver);
    }

    public void Remove(FavouriteDriver favouriteDriver)
    {
        _context.FavouriteDrivers.Remove(favouriteDriver);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
