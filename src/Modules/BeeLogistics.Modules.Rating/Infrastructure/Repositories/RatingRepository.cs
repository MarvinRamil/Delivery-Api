using BeeLogistics.Modules.Rating.Application.Interfaces;
using Domain = BeeLogistics.Modules.Rating.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Rating.Infrastructure.Repositories;

public class RatingRepository : IRatingRepository
{
    private readonly RatingDbContext _context;

    public RatingRepository(RatingDbContext context)
    {
        _context = context;
    }

    public async Task<Domain.Rating?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Ratings.FindAsync(new object[] { id }, ct);
    }

    public async Task<Domain.Rating?> GetByBookingIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await _context.Ratings
            .FirstOrDefaultAsync(r => r.BookingId == bookingId, ct);
    }

    public async Task<IReadOnlyList<Domain.Rating>> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.Ratings
            .Where(r => r.DriverId == driverId)
            .OrderByDescending(r => r.RatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Domain.Rating>> GetByCustomerIdAsync(Guid customerId, CancellationToken ct = default)
    {
        return await _context.Ratings
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.RatedAt)
            .ToListAsync(ct);
    }

    public void Add(Domain.Rating rating)
    {
        _context.Ratings.Add(rating);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}

public class DriverRatingRepository : IDriverRatingRepository
{
    private readonly RatingDbContext _context;

    public DriverRatingRepository(RatingDbContext context)
    {
        _context = context;
    }

    public async Task<Domain.DriverRating?> GetByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        return await _context.DriverRatings
            .FirstOrDefaultAsync(dr => dr.DriverId == driverId, ct);
    }

    public async Task<Domain.DriverRating> GetOrCreateByDriverIdAsync(Guid driverId, CancellationToken ct = default)
    {
        var driverRating = await GetByDriverIdAsync(driverId, ct);
        if (driverRating == null)
        {
            driverRating = new Domain.DriverRating(driverId);
            _context.DriverRatings.Add(driverRating);
            await _context.SaveChangesAsync(ct);
        }
        return driverRating;
    }

    public void Add(Domain.DriverRating driverRating)
    {
        _context.DriverRatings.Add(driverRating);
    }

    public void Update(Domain.DriverRating driverRating)
    {
        _context.DriverRatings.Update(driverRating);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
