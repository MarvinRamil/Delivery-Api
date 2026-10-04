using BeeLogistics.Modules.Giveaways.Domain;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Application.Interfaces;

public interface IRaffleDrawService
{
    Task<Result<List<GiveawayWinner>>> DrawWinnersAsync(Guid giveawayId, CancellationToken ct = default);
}
