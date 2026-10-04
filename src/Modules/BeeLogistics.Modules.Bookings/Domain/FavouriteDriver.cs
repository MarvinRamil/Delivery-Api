using System;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class FavouriteDriver : Entity
{
    public Guid CustomerId { get; private set; }
    public Guid DriverId { get; private set; }
    public DateTime AddedAt { get; private set; }

    private FavouriteDriver() { }

    public FavouriteDriver(Guid customerId, Guid driverId)
    {
        CustomerId = customerId;
        DriverId = driverId;
        AddedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
    }
}
