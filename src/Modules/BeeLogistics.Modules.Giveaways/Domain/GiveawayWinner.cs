using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Domain;

public class GiveawayWinner : Entity
{
    public Guid GiveawayId { get; private set; }
    public Guid GiveawayPrizeId { get; private set; }
    public Guid DriverId { get; private set; }
    public DateTime DrawnAt { get; private set; }

    public Giveaway Giveaway { get; private set; } = default!;
    public GiveawayPrize Prize { get; private set; } = default!;

    private GiveawayWinner() { }

    public GiveawayWinner(Guid giveawayId, Guid giveawayPrizeId, Guid driverId)
    {
        GiveawayId = giveawayId;
        GiveawayPrizeId = giveawayPrizeId;
        DriverId = driverId;
        DrawnAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}
