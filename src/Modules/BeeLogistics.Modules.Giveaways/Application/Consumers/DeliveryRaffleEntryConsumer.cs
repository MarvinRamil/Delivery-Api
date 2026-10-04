using BeeLogistics.Modules.Giveaways.Domain;
using BeeLogistics.Modules.Giveaways.Infrastructure;
using BeeLogistics.Shared.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace BeeLogistics.Modules.Giveaways.Application.Consumers;

/// <summary>
/// Listens for BookingCompletedEvent and automatically assigns a raffle entry to the driver
/// for any active giveaway configured with EntryMode = PerDelivery.
/// </summary>
public class DeliveryRaffleEntryConsumer : IConsumer<BookingCompletedEvent>
{
    private readonly IGiveawayRepository _repository;
    private readonly ILogger<DeliveryRaffleEntryConsumer> _logger;

    public DeliveryRaffleEntryConsumer(IGiveawayRepository repository, ILogger<DeliveryRaffleEntryConsumer> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<BookingCompletedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        // Find active giveaways that allow per-delivery entries
        var allGiveaways = await _repository.GetGiveawaysAsync(activeOnly: true, ct);
        var deliveryGiveaways = allGiveaways
            .Where(g => g.EntryMode == GiveawayEntryMode.PerDelivery && g.Status == GiveawayStatus.Active)
            .ToList();

        if (!deliveryGiveaways.Any())
            return;

        foreach (var giveaway in deliveryGiveaways)
        {
            // Idempotency check: did we already process this booking for this giveaway?
            bool alreadyEntered = await _repository.HasEntryForBookingAsync(giveaway.Id, msg.DriverId, msg.BookingId, ct);
            if (alreadyEntered)
            {
                _logger.LogInformation("Driver {DriverId} already has an entry for Booking {BookingId} in Giveaway {GiveawayId}. Skipping.", 
                    msg.DriverId, msg.BookingId, giveaway.Id);
                continue;
            }

            // Check if the driver has hit the maximum total entries for this giveaway
            int currentTotalEntries = await _repository.GetEntryCountAsync(giveaway.Id, msg.DriverId, ct);
            if (currentTotalEntries >= giveaway.MaxEntriesPerDriver)
            {
                _logger.LogInformation("Driver {DriverId} reached max entries ({Max}) for Giveaway {GiveawayId}. Skipping.",
                    msg.DriverId, giveaway.MaxEntriesPerDriver, giveaway.Id);
                continue;
            }

            // Add the entry
            var entry = new GiveawayEntry(giveaway.Id, msg.DriverId, GiveawayEntrySource.DeliveryCompletion, msg.BookingId);
            await _repository.AddEntryAsync(entry, ct);

            _logger.LogInformation("Awarded DeliveryRaffleEntry to Driver {DriverId} for Booking {BookingId} in Giveaway {GiveawayId}.",
                msg.DriverId, msg.BookingId, giveaway.Id);
        }
    }
}
