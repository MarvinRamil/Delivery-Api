using BeeLogistics.Modules.Giveaways.Domain;

namespace BeeLogistics.Modules.Giveaways.Infrastructure;

public interface IGiveawayRepository
{
    Task<List<Giveaway>> GetGiveawaysAsync(bool activeOnly, CancellationToken ct = default);
    Task<Giveaway?> GetGiveawayByIdAsync(Guid id, CancellationToken ct = default);
    Task<Giveaway> AddGiveawayAsync(Giveaway giveaway, CancellationToken ct = default);
    Task<Giveaway> UpdateGiveawayAsync(Giveaway giveaway, CancellationToken ct = default);
    Task SoftDeleteGiveawayAsync(Giveaway giveaway, string? deletedBy, CancellationToken ct = default);

    Task<GiveawayPrize> AddPrizeAsync(GiveawayPrize prize, CancellationToken ct = default);
    Task<List<GiveawayPrize>> GetPrizesAsync(Guid giveawayId, CancellationToken ct = default);
    Task DeletePrizeAsync(GiveawayPrize prize, CancellationToken ct = default);
    Task<GiveawayPrize?> GetPrizeByIdAsync(Guid prizeId, CancellationToken ct = default);

    Task<bool> HasEntryAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default);
    Task<int> GetEntryCountAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default);
    Task<int> GetEntriesTodayCountAsync(Guid giveawayId, Guid driverId, CancellationToken ct = default);
    Task<bool> HasEntryForBookingAsync(Guid giveawayId, Guid driverId, Guid bookingId, CancellationToken ct = default);
    Task<List<GiveawayEntry>> GetEntriesForGiveawayAsync(Guid giveawayId, CancellationToken ct = default);
    Task<GiveawayEntry> AddEntryAsync(GiveawayEntry entry, CancellationToken ct = default);

    Task<GiveawayWinner> AddWinnerAsync(GiveawayWinner winner, CancellationToken ct = default);
    Task<List<GiveawayWinner>> GetWinnersAsync(Guid giveawayId, CancellationToken ct = default);


    Task<List<Campaign>> GetCampaignsAsync(bool activeOnly, CancellationToken ct = default);
    Task<Campaign?> GetCampaignByIdAsync(Guid id, CancellationToken ct = default);
    Task<Campaign> AddCampaignAsync(Campaign campaign, CancellationToken ct = default);
    Task<Campaign> UpdateCampaignAsync(Campaign campaign, CancellationToken ct = default);
    Task SoftDeleteCampaignAsync(Campaign campaign, string? deletedBy, CancellationToken ct = default);
}
