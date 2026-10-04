using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Domain;

public class GiveawayEntry : Entity
{
    public Guid GiveawayId { get; private set; }
    public Guid DriverId { get; private set; }
    public DateTime EnteredAt { get; private set; }
    public GiveawayEntrySource Source { get; private set; }
    public Guid? BookingId { get; private set; }

    public Giveaway Giveaway { get; private set; } = default!;

    private GiveawayEntry() { }

    public GiveawayEntry(Guid giveawayId, Guid driverId, GiveawayEntrySource source = GiveawayEntrySource.Manual, Guid? bookingId = null)
    {
        if (source == GiveawayEntrySource.DeliveryCompletion && !bookingId.HasValue)
            throw new InvalidOperationException("BookingId is required for delivery-sourced entries.");

        GiveawayId = giveawayId;
        DriverId = driverId;
        Source = source;
        BookingId = bookingId;
        EnteredAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}
