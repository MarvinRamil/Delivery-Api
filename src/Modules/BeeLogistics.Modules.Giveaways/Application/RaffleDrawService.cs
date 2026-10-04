using System.Security.Cryptography;
using BeeLogistics.Modules.Giveaways.Application.Interfaces;
using BeeLogistics.Modules.Giveaways.Domain;
using BeeLogistics.Modules.Giveaways.Infrastructure;
using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Giveaways.Application;

public class RaffleDrawService : IRaffleDrawService
{
    private readonly IGiveawayRepository _repository;
    private readonly ILogger<RaffleDrawService> _logger;
    private readonly IPublishEndpoint? _publishEndpoint;

    public RaffleDrawService(
        IGiveawayRepository repository,
        ILogger<RaffleDrawService> logger,
        IPublishEndpoint? publishEndpoint = null)
    {
        _repository = repository;
        _logger = logger;
        _publishEndpoint = publishEndpoint;
    }

    public async Task<Result<List<GiveawayWinner>>> DrawWinnersAsync(Guid giveawayId, CancellationToken ct = default)
    {
        var giveaway = await _repository.GetGiveawayByIdAsync(giveawayId, ct);
        if (giveaway == null)
            return Result.Fail<List<GiveawayWinner>>("Giveaway not found.");

        if (giveaway.Status == GiveawayStatus.Drawn)
            return Result.Fail<List<GiveawayWinner>>("Giveaway has already been drawn.");

        if (giveaway.Status != GiveawayStatus.Active && giveaway.Status != GiveawayStatus.Closed)
            return Result.Fail<List<GiveawayWinner>>($"Cannot draw winners for a giveaway in status: {giveaway.Status}.");

        var prizes = await _repository.GetPrizesAsync(giveawayId, ct);
        if (!prizes.Any())
            return Result.Fail<List<GiveawayWinner>>("No prizes configured for this giveaway.");

        // Fetch all entries. Each entry is a single ticket.
        var allEntries = await _repository.GetEntriesForGiveawayAsync(giveawayId, ct);
        if (!allEntries.Any())
            return Result.Fail<List<GiveawayWinner>>("No entries found for this giveaway.");

        // Group by driver to ensure one driver doesn't win multiple times in the same draw
        var tickets = allEntries.Select(e => e.DriverId).ToList();
        var eligibleDrivers = new HashSet<Guid>(tickets);
        
        var winners = new List<GiveawayWinner>();
        
        // Loop through prizes ordered by Tier (1 is highest priority)
        foreach (var prize in prizes.OrderBy(p => p.Tier))
        {
            // Process the quantity of this prize
            for (int i = 0; i < prize.Quantity; i++)
            {
                // If we ran out of eligible drivers (or no tickets left), we stop drawing
                if (eligibleDrivers.Count == 0 || tickets.Count == 0)
                {
                    _logger.LogWarning("Ran out of eligible drivers while drawing for Giveaway {GiveawayId}. Prize: {PrizeName}, Quantity Shortfall: {Shortfall}", giveawayId, prize.Name, prize.Quantity - i);
                    break;
                }

                // Pick a random ticket
                int winningIndex = GetSecureRandomIndex(tickets.Count);
                var winningDriverId = tickets[winningIndex];

                // Create the winner record
                var winner = new GiveawayWinner(giveawayId, prize.Id, winningDriverId);
                winners.Add(winner);
                await _repository.AddWinnerAsync(winner, ct);

                _logger.LogInformation("Driver {DriverId} won prize {PrizeName} for Giveaway {GiveawayId}.", winningDriverId, prize.Name, giveawayId);

                // Remove the winning driver from the eligible list and remove ALL their tickets from the pool
                // (so they don't win again in this same draw)
                eligibleDrivers.Remove(winningDriverId);
                tickets.RemoveAll(d => d == winningDriverId);
            }
        }

        // Mark giveaway as drawn
        giveaway.MarkAsDrawn();
        await _repository.UpdateGiveawayAsync(giveaway, ct);

        // Notify the back-office backend (webhook per winner). Publish failures
        // must not undo a completed draw.
        if (_publishEndpoint != null)
        {
            foreach (var winner in winners)
            {
                try
                {
                    await _publishEndpoint.Publish(new GiveawayWinnerSelectedEvent(
                        giveawayId, winner.GiveawayPrizeId, winner.DriverId, DateTime.UtcNow), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to publish GiveawayWinnerSelectedEvent for giveaway {GiveawayId}", giveawayId);
                }
            }
        }

        return Result.Ok(winners);
    }

    /// <summary>
    /// Generates a cryptographically secure random integer between 0 (inclusive) and maxValue (exclusive).
    /// </summary>
    private int GetSecureRandomIndex(int maxValue)
    {
        if (maxValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxValue));

        return RandomNumberGenerator.GetInt32(maxValue);
    }
}
