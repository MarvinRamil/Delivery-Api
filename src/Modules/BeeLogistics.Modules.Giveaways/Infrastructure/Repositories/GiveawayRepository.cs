using BeeLogistics.Modules.Giveaways.Domain;
using Microsoft.EntityFrameworkCore;

namespace BeeLogistics.Modules.Giveaways.Infrastructure.Repositories;

public class GiveawayRepository : IGiveawayRepository
{
    private readonly GiveawaysDbContext _context;

    public GiveawayRepository(GiveawaysDbContext context)
    {
        _context = context;
    }

    public async Task<List<Giveaway>> GetGiveawaysAsync(bool activeOnly, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = _context.Giveaways.AsQueryable();
        if (activeOnly)
        {
            query = query.Where(x => x.IsActive && x.StartDate <= now && x.EndDate >= now);
        }

        return await query.OrderByDescending(x => x.StartDate).ToListAsync(ct);
    }

    public Task<Giveaway?> GetGiveawayByIdAsync(Guid id, CancellationToken ct = default)
        => _context.Giveaways.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<Giveaway> AddGiveawayAsync(Giveaway giveaway, CancellationToken ct = default)
    {
        await _context.Giveaways.AddAsync(giveaway, ct);
        await _context.SaveChangesAsync(ct);
        return giveaway;
    }

    public async Task<Giveaway> UpdateGiveawayAsync(Giveaway giveaway, CancellationToken ct = default)
    {
        _context.Giveaways.Update(giveaway);
        await _context.SaveChangesAsync(ct);
        return giveaway;
    }

    public async Task SoftDeleteGiveawayAsync(Giveaway giveaway, string? deletedBy, CancellationToken ct = default)
    {
        giveaway.SoftDelete(deletedBy);
        _context.Giveaways.Update(giveaway);
        await _context.SaveChangesAsync(ct);
    }

    public Task<bool> HasEntryAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default)
        => _context.GiveawayEntries.AnyAsync(x => x.GiveawayId == giveawayId && x.DriverId == driverId, ct);

    public Task<int> GetEntryCountAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default)
        => _context.GiveawayEntries.CountAsync(x => x.GiveawayId == giveawayId && x.DriverId == driverId, ct);

    public Task<int> GetEntriesTodayCountAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return _context.GiveawayEntries.CountAsync(
            x => x.GiveawayId == giveawayId && x.DriverId == driverId && x.EnteredAt >= today, ct);
    }

    public Task<bool> HasEntryForBookingAsync(Guid giveawayId, Guid driverId, Guid bookingId, CancellationToken ct = default)
        => _context.GiveawayEntries.AnyAsync(
            x => x.GiveawayId == giveawayId && x.DriverId == driverId && x.BookingId == bookingId, ct);

    public Task<List<GiveawayEntry>> GetEntriesForGiveawayAsync(Guid giveawayId, CancellationToken ct = default)
        => _context.GiveawayEntries.Where(x => x.GiveawayId == giveawayId).ToListAsync(ct);

    public async Task<GiveawayEntry> AddEntryAsync(GiveawayEntry entry, CancellationToken ct = default)
    {
        await _context.GiveawayEntries.AddAsync(entry, ct);
        await _context.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<GiveawayPrize> AddPrizeAsync(GiveawayPrize prize, CancellationToken ct = default)
    {
        await _context.GiveawayPrizes.AddAsync(prize, ct);
        await _context.SaveChangesAsync(ct);
        return prize;
    }

    public Task<List<GiveawayPrize>> GetPrizesAsync(Guid giveawayId, CancellationToken ct = default)
        => _context.GiveawayPrizes
            .Where(x => x.GiveawayId == giveawayId)
            .OrderBy(x => x.Tier)
            .ToListAsync(ct);

    public async Task DeletePrizeAsync(GiveawayPrize prize, CancellationToken ct = default)
    {
        _context.GiveawayPrizes.Remove(prize);
        await _context.SaveChangesAsync(ct);
    }

    public Task<GiveawayPrize?> GetPrizeByIdAsync(Guid prizeId, CancellationToken ct = default)
        => _context.GiveawayPrizes.FirstOrDefaultAsync(x => x.Id == prizeId, ct);

    public async Task<GiveawayWinner> AddWinnerAsync(GiveawayWinner winner, CancellationToken ct = default)
    {
        await _context.GiveawayWinners.AddAsync(winner, ct);
        await _context.SaveChangesAsync(ct);
        return winner;
    }

    public Task<List<GiveawayWinner>> GetWinnersAsync(Guid giveawayId, CancellationToken ct = default)
        => _context.GiveawayWinners
            .Include(x => x.Prize)
            .Where(x => x.GiveawayId == giveawayId)
            .OrderBy(x => x.Prize.Tier)
            .ToListAsync(ct);

    public async Task<List<Campaign>> GetCampaignsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var query = _context.Campaigns.AsQueryable();
        if (activeOnly)
        {
            query = query.Where(x => x.IsActive && x.StartDate <= now && x.EndDate >= now);
        }

        return await query.OrderByDescending(x => x.StartDate).ToListAsync(ct);
    }

    public Task<Campaign?> GetCampaignByIdAsync(Guid id, CancellationToken ct = default)
        => _context.Campaigns.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<Campaign> AddCampaignAsync(Campaign campaign, CancellationToken ct = default)
    {
        await _context.Campaigns.AddAsync(campaign, ct);
        await _context.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task<Campaign> UpdateCampaignAsync(Campaign campaign, CancellationToken ct = default)
    {
        _context.Campaigns.Update(campaign);
        await _context.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task SoftDeleteCampaignAsync(Campaign campaign, string? deletedBy, CancellationToken ct = default)
    {
        campaign.SoftDelete(deletedBy);
        _context.Campaigns.Update(campaign);
        await _context.SaveChangesAsync(ct);
    }
}
